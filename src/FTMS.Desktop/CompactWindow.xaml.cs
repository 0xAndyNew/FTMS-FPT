using System.IO;
using System.ComponentModel;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FTMS.Application;
using FTMS.Domain;
using FTMS.Infrastructure;
using Microsoft.Web.WebView2.Core;

namespace FTMS.Desktop;

public partial class CompactWindow : Window
{
    private const string FtmsUrl = "https://ftms.fpt.net/ihub/list?tab=2";
    private readonly SettingsStore _settingsStore = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _refreshTimer = new();
    private Task? _telegramLoopTask;
    private readonly HttpClient _http;
    private readonly string _stateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FTMS.Companion");
    private TelegramCallbackReceiver? _telegramReceiver;
    private TicketMonitor? _monitor;
    private WebViewFtmsClient? _ftmsClient;
    private WebViewLoginRecovery? _displayLoginRecovery;
    private WebViewFtmsClient? _visibleIdentityClient;
    private CancellationTokenSource? _accountLifetime;
    private long? _expectedAccountId;
    private long? _activeAccountId;
    private int _visibleNavigationGeneration;
    private int _hiddenReloadAttempts;
    private bool _monitorStarting;
    private bool _monitorStarted;
    private bool _isListPage;
    private bool _settingsOpen;
    private bool _refreshInProgress;
    private bool _visibleNavigationInProgress;
    private bool _manualLoginNotificationShown;
    private bool _checkingTelegram;
    private Task _statusMutationSync = Task.CompletedTask;
    private DateTimeOffset _lastUserActivity = DateTimeOffset.MinValue;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _allowClose;

    public CompactWindow()
    {
        InitializeComponent(); _settingsStore.Load(); _http = TelegramHttpClientFactory.Create(() => _settingsStore.Current); Loaded += InitializeAsync;
        Closed += (_, _) => { _lifetime.Cancel(); _accountLifetime?.Cancel(); _refreshTimer.Stop(); _http.Dispose(); _trayIcon?.Dispose(); };
        Closing += OnWindowClosing;
        _refreshTimer.Tick += (_, _) => RunAutoRefresh();
        InitializeTrayIcon();
    }

    private static void BlockFtmsBot(CoreWebView2 webView, CoreWebView2Environment environment)
    {
        webView.AddWebResourceRequestedFilter(FtmsBotBlockerScript.Source, CoreWebView2WebResourceContext.Script);
        webView.WebResourceRequested += (_, args) =>
        {
            if (!args.Request.Uri.StartsWith(FtmsBotBlockerScript.Source, StringComparison.OrdinalIgnoreCase)) return;
            args.Response = environment.CreateWebResourceResponse(Stream.Null, 403, "Blocked", "Content-Type: application/javascript");
        };
    }

    private void InitializeTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Mở FTMS Companion", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Thoát", null, (_, _) =>
        {
            _allowClose = true;
            Dispatcher.Invoke(Close);
        });
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            Text = "FTMS Companion",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        Hide();
        if (_trayIcon is not null)
        {
            _trayIcon.BalloonTipTitle = "FTMS Companion vẫn đang chạy";
            _trayIcon.BalloonTipText = "Ứng dụng tiếp tục theo dõi ticket và gửi thông báo Telegram.";
            _trayIcon.ShowBalloonTip(2000);
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private async void InitializeAsync(object sender, RoutedEventArgs e)
    {
        var root = _stateDirectory;
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(root, "WebView2"));
        await FtmsWebView.EnsureCoreWebView2Async(environment);
        await MonitorWebView.EnsureCoreWebView2Async(environment);
        BlockFtmsBot(FtmsWebView.CoreWebView2, environment);
        BlockFtmsBot(MonitorWebView.CoreWebView2, environment);
        FtmsWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        FtmsWebView.CoreWebView2.WebResourceResponseReceived += OnVisibleFtmsResponseReceived;
        FtmsWebView.CoreWebView2.NavigationStarting += (_, args) =>
        {
            _visibleNavigationInProgress = true;
            _isListPage = false;
            MarkUserActivity();
            _visibleNavigationGeneration++;
            if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var next) ||
                !WebViewLoginRecovery.IsFtmsIhubUri(next)) DeactivateAccount();
        };
        FtmsWebView.CoreWebView2.SourceChanged += (_, _) => UpdateCurrentPage();
        MonitorWebView.NavigationCompleted += OnMonitorNavigationCompleted;
        await FtmsWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(FtmsUserActivityScript.Value);
        await FtmsWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(FtmsStatusMutationScript.Value);
        await FtmsWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(FtmsStickyPagerScript.Value);
        await FtmsWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(FtmsBotBlockerScript.Value);
        await MonitorWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(FtmsBotBlockerScript.Value);
        _displayLoginRecovery = new WebViewLoginRecovery(FtmsWebView, new Uri(FtmsUrl));
        _displayLoginRecovery.StatusChanged += OnLoginRecoveryStatusChanged;
        _ftmsClient = new WebViewFtmsClient(MonitorWebView, FtmsUrl);
        _visibleIdentityClient = new WebViewFtmsClient(FtmsWebView, FtmsUrl);
        _telegramReceiver = new TelegramCallbackReceiver(_stateDirectory,
            () => (_settingsStore.Current.TelegramToken, _settingsStore.Current.TelegramChatId),
            _http, ClaimTicketFromTelegramAsync,
            message => Dispatcher.BeginInvoke(() => MonitorText.Text = TelegramErrorSanitizer.Sanitize(
                message, _settingsStore.Current.TelegramToken)));
        _ftmsClient.LoginRecoveryStatusChanged += OnMonitorLoginRecoveryStatusChanged;
        ApplyRefreshSettings();
        _telegramLoopTask = Task.Run(() => RunTelegramLoopAsync(_lifetime.Token));
        FtmsWebView.Source = new Uri(FtmsUrl);
        MonitorWebView.Source = new Uri(FtmsUrl);
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var payload = JsonDocument.Parse(e.WebMessageAsJson);
            if (payload.RootElement.ValueKind == JsonValueKind.String)
            {
                if (payload.RootElement.GetString() == "ftms-user-activity") MarkUserActivity();
                return;
            }
            if (payload.RootElement.ValueKind != JsonValueKind.Object ||
                !payload.RootElement.TryGetProperty("type", out var type)) return;
            var mutationType = type.GetString();
            if (mutationType is not ("ftms-status-mutation" or "ftms-assignment-mutation")) return;
            var code = payload.RootElement.TryGetProperty("code", out var codeValue) ? codeValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(code)) return;
            var detectedAt = payload.RootElement.TryGetProperty("detectedAt", out var detectedValue) && detectedValue.TryGetInt64(out var parsedAt)
                ? parsedAt : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var actor = payload.RootElement.TryGetProperty("actor", out var actorValue) ? actorValue.GetString() : null;
            if (mutationType == "ftms-assignment-mutation")
            {
                if (!payload.RootElement.TryGetProperty("assigneeId", out var assigneeValue) ||
                    !assigneeValue.TryGetInt64(out var assigneeId) || assigneeId <= 0) return;
                var assigneeName = payload.RootElement.TryGetProperty("assigneeName", out var nameVal) ? nameVal.GetString() : null;
                TicketStatus? assignmentStatus = null;
                if (payload.RootElement.TryGetProperty("status", out var stVal) && stVal.TryGetInt32(out var parsedSt) && Enum.IsDefined(typeof(TicketStatus), parsedSt))
                    assignmentStatus = (TicketStatus)parsedSt;
                QueueAssignmentMutationSync(code, assigneeId, assigneeName, actor, assignmentStatus);
                return;
            }
            if (!payload.RootElement.TryGetProperty("status", out var statusValue) ||
                !statusValue.TryGetInt32(out var status) || !Enum.IsDefined(typeof(TicketStatus), status)) return;
            TicketStatus? prevStatus = null;
            if (payload.RootElement.TryGetProperty("previousStatus", out var prevVal) && prevVal.TryGetInt32(out var parsedPrev) && Enum.IsDefined(typeof(TicketStatus), parsedPrev))
                prevStatus = (TicketStatus)parsedPrev;
            QueueStatusMutationSync(code, (TicketStatus)status, prevStatus, actor, detectedAt);
        }
        catch (Exception ex) { MonitorText.Text = $"L\u1ed7i \u0111\u1ed3ng b\u1ed9 th\u1eddi gian th\u1ef1c: {ex.Message}"; }
    }

    private void QueueStatusMutationSync(string code, TicketStatus expectedStatus, TicketStatus? previousStatus, string? actor, long detectedAt)
    {
        QueueMutationSync(async (monitor, cancellationToken) =>
        {
            await monitor.ApplyConfirmedStatusMutationAsync(code, expectedStatus, previousStatus, actor, cancellationToken);
            await monitor.SyncUntilStatusAsync(code, expectedStatus, cancellationToken);
            if (monitor.TryGetTrackedStatus(code, out var status) && status == expectedStatus) return;
            await Dispatcher.InvokeAsync(() => MonitorText.Text =
                $"\u0110ang ch\u1edd FTMS x\u00e1c nh\u1eadn {code} \u2192 {expectedStatus.DisplayName()} ({detectedAt}).");
        });
    }

    private void QueueAssignmentMutationSync(string code, long expectedAssigneeId, string? expectedAssigneeName, string? actor, TicketStatus? status)
    {
        QueueMutationSync(async (monitor, cancellationToken) =>
        {
            await monitor.ApplyConfirmedAssignmentMutationAsync(code, expectedAssigneeId, expectedAssigneeName, actor, status, cancellationToken);
            await monitor.SyncUntilAssignmentAsync(code, expectedAssigneeId, cancellationToken);
        });
    }

    private void QueueMutationSync(Func<TicketMonitor, CancellationToken, Task> sync)
    {
        _statusMutationSync = _statusMutationSync.ContinueWith(async _ =>
        {
            var monitor = _monitor;
            var accountLifetime = _accountLifetime;
            if (monitor is null || accountLifetime?.IsCancellationRequested != false) return;
            await sync(monitor, accountLifetime.Token);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    private async void OnVisibleFtmsResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        var monitor = _monitor;
        var accountLifetime = _accountLifetime;
        var accountId = _activeAccountId;
        if (!_monitorStarted || monitor is null || accountLifetime is null ||
            accountId is null || accountLifetime.IsCancellationRequested) return;
        if (e.Response.StatusCode is < 200 or >= 300) return;
        if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Host, "ftms.fpt.net", StringComparison.OrdinalIgnoreCase)) return;
        var path = uri.AbsolutePath;
        if (!path.Contains("GetListRequestV12", StringComparison.OrdinalIgnoreCase) &&
            !path.Contains("GetListCasesV12", StringComparison.OrdinalIgnoreCase) &&
            !path.Contains("GetListAlarm", StringComparison.OrdinalIgnoreCase) &&
            !path.Contains("GetListCasesRequest", StringComparison.OrdinalIgnoreCase)) return;

        if (_activeAccountId != accountId) return;
        monitor.TriggerSync();
    }

    private async void OnMonitorNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_lifetime.IsCancellationRequested || _ftmsClient is null) return;
        if (!e.IsSuccess)
        {
            MonitorText.Text = $"WebView giám sát không mở được FTMS: {e.WebErrorStatus}";
            return;
        }

        var uri = MonitorWebView.Source;
        if (uri is null) return;
        if (WebViewLoginRecovery.IsFtmsIhubUri(uri))
        {
            _ftmsClient.NotifyTargetReached();
            if (_expectedAccountId is null) return;
            var expectedAccountId = _expectedAccountId.Value;
            CurrentUserIdentity? hiddenIdentity;
            try { hiddenIdentity = await _ftmsClient.GetCurrentUserAsync(_lifetime.Token); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            if (_expectedAccountId != expectedAccountId) return;
            if (hiddenIdentity?.UserId != expectedAccountId)
            {
                if (_hiddenReloadAttempts++ < 2)
                    MonitorWebView.CoreWebView2?.Reload();
                else
                    MonitorText.Text = "Tài khoản WebView giám sát không khớp; đã tạm dừng để tránh lẫn dữ liệu.";
                return;
            }
            _hiddenReloadAttempts = 0;
            await StartMonitorOnceAsync(expectedAccountId);
            return;
        }
        if (WebViewLoginRecovery.IsLoginUri(uri) || WebViewLoginRecovery.IsAdfsUri(uri))
        {
            try { await _ftmsClient.BeginLoginRecoveryAsync(_lifetime.Token); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex) { MonitorText.Text = $"Giám sát FTMS cần đăng nhập: {ex.Message}"; }
        }
    }

    private async Task CheckAndStartMonitorFromVisibleIdentityAsync(long expectedAccountId)
    {
        if (_lifetime.IsCancellationRequested || _ftmsClient is null) return;
        var uri = MonitorWebView.Source;
        if (uri is null || !WebViewLoginRecovery.IsFtmsIhubUri(uri)) return;

        CurrentUserIdentity? hiddenIdentity;
        try { hiddenIdentity = await _ftmsClient.GetCurrentUserAsync(_lifetime.Token); }
        catch { return; }

        if (_expectedAccountId != expectedAccountId) return;
        if (hiddenIdentity?.UserId == expectedAccountId)
        {
            _hiddenReloadAttempts = 0;
            await StartMonitorOnceAsync(expectedAccountId);
        }
        else
        {
            MonitorWebView.CoreWebView2?.Reload();
        }
    }

    private async void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _visibleNavigationInProgress = false;
        _refreshInProgress = false;
        if (_lifetime.IsCancellationRequested || _displayLoginRecovery is null) return;
        if (!e.IsSuccess)
        {
            SessionText.Text = "L\u1ed7i k\u1ebft n\u1ed1i";
            MonitorText.Text = $"Kh\u00f4ng th\u1ec3 m\u1edf FTMS: {e.WebErrorStatus}";
            return;
        }

        try
        {
            var uri = FtmsWebView.Source;
            if (uri is null) return;

            if (WebViewLoginRecovery.IsFtmsIhubUri(uri))
            {
                UpdateCurrentPage();
                _manualLoginNotificationShown = false;
                _displayLoginRecovery.NotifyTargetReached();
                _visibleIdentityClient!.NotifyTargetReached();
                var navigationGeneration = _visibleNavigationGeneration;
                CurrentUserIdentity? visibleIdentity = null;
                for (var attempt = 0; attempt < 3 && visibleIdentity is null; attempt++)
                {
                    visibleIdentity = await _visibleIdentityClient.GetCurrentUserAsync(_lifetime.Token);
                    if (visibleIdentity is null) await Task.Delay(1000, _lifetime.Token);
                }
                if (navigationGeneration != _visibleNavigationGeneration) return;
                if (visibleIdentity is null)
                {
                    // A ticket detail can omit the list page's identity globals. Keep an already
                    // verified session; logout/navigation away from iHUB cancels it separately.
                    if (_expectedAccountId is null)
                    {
                        SetSessionStatus("Chưa xác định tài khoản", "#D9A441");
                        MonitorText.Text = "Chưa xác định được tài khoản FTMS; giám sát đang tạm dừng.";
                    }
                    return;
                }
                SetSessionStatus("Đã kết nối", "#4AA47B");
                if (_expectedAccountId != visibleIdentity.UserId)
                {
                    DeactivateAccount();
                    UpdateDashboard(new DashboardSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, visibleIdentity));
                    _expectedAccountId = visibleIdentity.UserId;
                    _hiddenReloadAttempts = 0;
                    if (MonitorWebView.Source is null ||
                        !WebViewLoginRecovery.IsFtmsIhubUri(MonitorWebView.Source))
                        MonitorWebView.Source = new Uri(FtmsUrl);
                    else
                        _ = CheckAndStartMonitorFromVisibleIdentityAsync(visibleIdentity.UserId);
                }
                return;
            }

            if (WebViewLoginRecovery.IsLoginUri(uri) || WebViewLoginRecovery.IsAdfsUri(uri))
            {
                SetSessionStatus("\u0110ang \u0111\u0103ng nh\u1eadp", "#D9A441");
                await _displayLoginRecovery.BeginAsync(_lifetime.Token);
                return;
            }

            SetSessionStatus("\u0110ang k\u1ebft n\u1ed1i", "#D9A441");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { MonitorText.Text = $"Kh\u00f4ng th\u1ec3 t\u1ef1 \u0111\u1ed9ng \u0111\u0103ng nh\u1eadp: {ex.Message}"; }
    }

    private void OnLoginRecoveryStatusChanged(LoginRecoveryStatus status)
    {
        Dispatcher.Invoke(() =>
        {
            MonitorText.Text = status.Message;
            SetSessionStatus(status.RequiresUserAction ? "C\u1ea7n \u0111\u0103ng nh\u1eadp" : "\u0110ang \u0111\u0103ng nh\u1eadp", "#D9A441");
            if (!status.RequiresUserAction || _manualLoginNotificationShown || _trayIcon is null) return;
            _manualLoginNotificationShown = true;
            _trayIcon.BalloonTipTitle = "FTMS c\u1ea7n x\u00e1c th\u1ef1c";
            _trayIcon.BalloonTipText = "Vui l\u00f2ng ho\u00e0n t\u1ea5t \u0111\u0103ng nh\u1eadp ho\u1eb7c MFA trong c\u1eeda s\u1ed5 FTMS Companion.";
            _trayIcon.ShowBalloonTip(4000);
        });
    }

    private void OnMonitorLoginRecoveryStatusChanged(LoginRecoveryStatus status)
    {
        Dispatcher.Invoke(() => MonitorText.Text = status.RequiresUserAction
            ? "Giám sát cần xác thực FTMS; hãy đăng nhập ở trang FTMS đang hiển thị."
            : status.Message);
    }

    private void SetSessionStatus(string text, string color)
    {
        SessionText.Text = text;
        StatusDot.Fill = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
    }

    private void DeactivateAccount()
    {
        _accountLifetime?.Cancel();
        _accountLifetime = null;
        _expectedAccountId = null;
        _activeAccountId = null;
        _monitor = null;
        _monitorStarted = false;
        _monitorStarting = false;
        UpdateDashboard(new DashboardSummary(0, 0, 0, 0, 0, 0, 0, 0, 0));
    }

    private async Task StartMonitorOnceAsync(long accountId)
    {
        if (_monitorStarted || _monitorStarting || _expectedAccountId != accountId || _ftmsClient is null) return;
        _monitorStarting = true;
        var accountLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _accountLifetime = accountLifetime;
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FTMS.Companion");
            var databasePath = Path.Combine(root, $"ftms-user-{accountId}.db");
            var settings = new AppSettings { FtmsUrl = FtmsUrl, PollIntervalSeconds = 2, IdleDelaySeconds = 0 };
            var telegram = new TelegramOutboxSender(databasePath,
                () => (_settingsStore.Current.TelegramToken, _settingsStore.Current.TelegramChatId), _http);
            var monitor = new TicketMonitor(_ftmsClient, new SqliteTicketStore(databasePath),
                telegram, new TicketChangeDetector(), settings);
            monitor.StatusChanged += message => Dispatcher.BeginInvoke(() =>
            {
                if (_activeAccountId == accountId) MonitorText.Text = message;
            });
            monitor.SummaryChanged += summary => Dispatcher.BeginInvoke(() =>
            {
                if (_activeAccountId == accountId) UpdateDashboard(summary);
            });
            monitor.DailyCleanupCompleted += () => DailyLogCleaner.Clean(root);
            await Task.Run(() => monitor.InitializeAsync(accountLifetime.Token));
            if (accountLifetime.IsCancellationRequested || _expectedAccountId != accountId) return;
            _monitor = monitor;
            _activeAccountId = accountId;
            _monitorStarted = true;
            MonitorText.Text = "Bắt đầu theo dõi ticket";
            _ = Task.Run(() => monitor.RunAsync(accountLifetime.Token));
        }
        catch (OperationCanceledException) when (accountLifetime.IsCancellationRequested) { }
        catch (Exception ex) { MonitorText.Text = $"Không thể khởi tạo giám sát: {ex.Message}"; }
        finally
        {
            if (ReferenceEquals(_accountLifetime, accountLifetime)) _monitorStarting = false;
        }
    }

    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        MarkUserActivity();
        _settingsOpen = true;
        try
        {
            if (new CompactSettingsWindow(_settingsStore) { Owner = this }.ShowDialog() == true)
                ApplyRefreshSettings();
        }
        finally { _settingsOpen = false; MarkUserActivity(); }
    }
    private void OpenFtms(object sender, RoutedEventArgs e) => FtmsWebView.Source = new Uri(FtmsUrl);
    private void ReloadFtms(object sender, RoutedEventArgs e)
    {
        MarkUserActivity();
        var monitor = _monitor;
        var accountLifetime = _accountLifetime;
        if (monitor is not null && accountLifetime is not null && !accountLifetime.IsCancellationRequested)
            _ = Task.Run(() => monitor.SyncNowAsync(forceHistory: true, accountLifetime.Token));

        if (FtmsWebView.CoreWebView2 is null || _refreshInProgress) return;
        _refreshInProgress = true;
        MonitorText.Text = "Đang tải lại trang FTMS";
        try { FtmsWebView.Reload(); }
        catch (Exception ex)
        {
            _refreshInProgress = false;
            MonitorText.Text = $"Không thể tải lại trang FTMS: {ex.Message}";
        }
    }
    private void OnUserActivity(object sender, InputEventArgs e) => MarkUserActivity();
    private void MarkUserActivity() => _lastUserActivity = DateTimeOffset.UtcNow;
    private bool IsUserBusy() => _settingsOpen || _visibleNavigationInProgress || !_isListPage ||
        DateTimeOffset.UtcNow - _lastUserActivity < TimeSpan.FromSeconds(15);

    private void UpdateCurrentPage()
    {
        _isListPage = Uri.TryCreate(FtmsWebView.CoreWebView2?.Source, UriKind.Absolute, out var uri) &&
            WebViewLoginRecovery.IsFtmsIhubUri(uri) &&
            string.Equals(uri.AbsolutePath.TrimEnd('/'), "/ihub/list", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyRefreshSettings()
    {
        _refreshTimer.Stop(); if (!_settingsStore.Current.AutoRefreshEnabled) return;
        _refreshTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_settingsStore.Current.AutoRefreshSeconds, 5, 3600)); _refreshTimer.Start();
    }

    private async void RunAutoRefresh()
    {
        if (!IsVisible || IsUserBusy()) return;
        await RefreshTicketGridAsync();
    }

    private async Task RefreshTicketGridAsync()
    {
        if (FtmsWebView.CoreWebView2 is null || !_isListPage || _refreshInProgress) return;
        _refreshInProgress = true;
        const string script = """
            (() => {
              if (location.hostname.toLowerCase() !== 'ftms.fpt.net' ||
                  !/^\/ihub\/list\/?$/i.test(location.pathname)) return false;
              if (Date.now() - (window.__ftmsCompanionLastInputAt || 0) < 15000) return false;
              const refresh = document.querySelector('a.k-pager-refresh.k-link');
              if (!refresh) return false;
              refresh.click();
              return true;
            })()
            """;
        try { await FtmsWebView.ExecuteScriptAsync(script); }
        catch (Exception ex) { MonitorText.Text = $"Không thể làm mới danh sách: {ex.Message}"; }
        finally { _refreshInProgress = false; }
    }

    private async Task RunTelegramLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var receiver = _telegramReceiver;
            var accountId = _activeAccountId;
            if (receiver is null || accountId is null)
            {
                try { await Task.Delay(1000, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                continue;
            }

            try
            {
                await receiver.CheckAsync(cancellationToken, timeoutSeconds: 25);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    MonitorText.Text = "Lỗi thao tác Telegram: " +
                        TelegramErrorSanitizer.Sanitize(ex.Message, _settingsStore.Current.TelegramToken);
                });
                try { await Task.Delay(3000, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            }
        }
    }

    private async Task CheckTelegramActionsAsync()
    {
        if (_checkingTelegram || _telegramReceiver is null || _activeAccountId is null) return;
        _checkingTelegram = true;
        try { await _telegramReceiver.CheckAsync(_lifetime.Token, timeoutSeconds: 0); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            MonitorText.Text = $"Lỗi thao tác Telegram: " +
                TelegramErrorSanitizer.Sanitize(ex.Message, _settingsStore.Current.TelegramToken);
        }
        finally { _checkingTelegram = false; }
    }

    private async Task<TicketClaimResult> ClaimTicketFromTelegramAsync(string code, CancellationToken cancellationToken)
    {
        var accountId = _activeAccountId;
        var accountLifetime = _accountLifetime;
        var client = _ftmsClient;
        if (accountId is not long activeAccountId || accountLifetime?.IsCancellationRequested != false || client is null)
            return new TicketClaimResult(TicketClaimStatus.RetryableFailure, "FTMS Companion chưa sẵn sàng.");

        var result = await client.ClaimTicketAsync(code, activeAccountId, cancellationToken);
        if (result.IsSuccess && _activeAccountId == activeAccountId && _monitor is not null)
        {
            try
            {
                await _monitor.ApplyConfirmedAssignmentMutationAsync(code, activeAccountId, null, null, null, accountLifetime.Token);
                await Task.Run(() => _monitor.SyncUntilAssignmentAsync(code, activeAccountId, accountLifetime.Token), accountLifetime.Token);
            }
            catch (OperationCanceledException) when (accountLifetime.IsCancellationRequested) { }
            catch (Exception ex) { MonitorText.Text = $"Đã nhận {code}, nhưng chưa đồng bộ được: {ex.Message}"; }
        }
        return result;
    }

    private void UpdateDashboard(DashboardSummary s)
    {
        GlobalNewValue.Text = s.New.ToString("N0");

        if (!s.HasCurrentUser)
        {
            CurrentUserName.Text = "Đang nhận diện người dùng…";
            CurrentUserName.ToolTip = null;
            PersonalTotalValue.Text = "—";
            PersonalAssignedValue.Text = "—";
            PersonalInProgressValue.Text = "—";
            PersonalPausedValue.Text = "—";
            PersonalClosedTodayValue.Text = "—";
            SlaRiskValue.Text = "—";
            SlaViolatedValue.Text = "—";
            System.Windows.Automation.AutomationProperties.SetName(DashboardCard,
                $"Ticket mới tất cả: {s.New}. Đang nhận diện người dùng. Chưa có dữ liệu SLA cá nhân.");
            return;
        }

        var userName = string.IsNullOrWhiteSpace(s.CurrentUser!.UserName) ? "Người dùng hiện tại" : s.CurrentUser.UserName;
        CurrentUserName.Text = userName;
        CurrentUserName.ToolTip = userName;
        PersonalTotalValue.Text = s.PersonalWorkloadTotal.ToString("N0");
        PersonalAssignedValue.Text = s.PersonalAssigned.ToString("N0");
        PersonalInProgressValue.Text = s.PersonalInProgress.ToString("N0");
        PersonalPausedValue.Text = s.PersonalPaused.ToString("N0");
        PersonalClosedTodayValue.Text = s.PersonalClosedToday.ToString("N0");
        SlaRiskValue.Text = s.SlaRisk.ToString("N0");
        SlaViolatedValue.Text = s.SlaViolated.ToString("N0");
        System.Windows.Automation.AutomationProperties.SetName(DashboardCard,
            $"Ticket mới tất cả: {s.New}. Công việc của {userName}: {s.PersonalAssigned} phân công, " +
            $"{s.PersonalInProgress} đang thực hiện, {s.PersonalPaused} tạm ngưng, {s.PersonalClosedToday} đã đóng hôm nay. " +
            $"SLA của {userName}: {s.SlaRisk} sắp hạn, {s.SlaViolated} quá hạn.");
    }

}
