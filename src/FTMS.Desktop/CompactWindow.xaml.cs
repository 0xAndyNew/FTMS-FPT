using System.IO;
using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Text;
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
    private const string FtmsReactUrl = "https://ftms.fpt.net/ihub/react/list?referrer=menu";
    private const string FtmsLegacyUrl = "https://ftms.fpt.net/ihub/list?tab=2";
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
    private CurrentUserIdentity? _activeUser;
    private int _visibleNavigationGeneration;
    private int _hiddenReloadAttempts;
    private bool _monitorStarting;
    private bool _monitorStarted;
    private bool _isListPage;
    private bool _settingsOpen;
    private bool _visibleNavigationInProgress;
    private bool _manualLoginNotificationShown;
    private bool _checkingTelegram;
    private bool _wasLoggingIn;
    private Task _statusMutationSync = Task.CompletedTask;
    private DateTimeOffset _lastUserActivity = DateTimeOffset.MinValue;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _allowClose;

    public CompactWindow()
    {
        InitializeComponent(); _settingsStore.Load(); _http = TelegramHttpClientFactory.Create(() => _settingsStore.Current); Loaded += InitializeAsync;
        Closed += (_, _) => { _lifetime.Cancel(); _accountLifetime?.Cancel(); _refreshTimer.Stop(); _http.Dispose(); _trayIcon?.Dispose(); };
        Closing += OnWindowClosing;
        StateChanged += OnWindowStateChanged;
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
        SetWebViewMemoryLevel(CoreWebView2MemoryUsageTargetLevel.Low);
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
        SetWebViewMemoryLevel(CoreWebView2MemoryUsageTargetLevel.Normal);
        Activate();
    }

    private void SetWebViewMemoryLevel(CoreWebView2MemoryUsageTargetLevel level)
    {
        try
        {
            if (FtmsWebView.CoreWebView2 is not null)
                FtmsWebView.CoreWebView2.MemoryUsageTargetLevel = level;
            if (MonitorWebView.CoreWebView2 is not null)
                MonitorWebView.CoreWebView2.MemoryUsageTargetLevel = level;
        }
        catch { }
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        SetWebViewMemoryLevel(WindowState == WindowState.Minimized
            ? CoreWebView2MemoryUsageTargetLevel.Low
            : CoreWebView2MemoryUsageTargetLevel.Normal);
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
        FtmsWebView.CoreWebView2.AddWebResourceRequestedFilter("https://ftms.fpt.net/*", CoreWebView2WebResourceContext.All);
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
        _displayLoginRecovery = new WebViewLoginRecovery(FtmsWebView, new Uri(FtmsReactUrl));
        _displayLoginRecovery.StatusChanged += OnLoginRecoveryStatusChanged;
        _ftmsClient = new WebViewFtmsClient(MonitorWebView, FtmsLegacyUrl);
        _visibleIdentityClient = new WebViewFtmsClient(FtmsWebView, FtmsReactUrl);
        _telegramReceiver = new TelegramCallbackReceiver(_stateDirectory,
            () => (_settingsStore.Current.TelegramToken, _settingsStore.Current.TelegramChatId),
            _http, ClaimTicketFromTelegramAsync,
            PauseTicketFromTelegramAsync,
            HandleTelegramCommandAsync,
            message => Dispatcher.BeginInvoke(() => MonitorText.Text = TelegramErrorSanitizer.Sanitize(
                message, _settingsStore.Current.TelegramToken)));
        _ftmsClient.LoginRecoveryStatusChanged += OnMonitorLoginRecoveryStatusChanged;
        ApplyRefreshSettings();
        _telegramLoopTask = Task.Run(() => RunTelegramLoopAsync(_lifetime.Token));
        FtmsWebView.Source = new Uri(FtmsReactUrl);
        MonitorWebView.Source = new Uri(FtmsLegacyUrl);
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
            if (mutationType == "ftms-general-mutation")
            {
                _monitor?.TriggerSync();
                return;
            }
            if (mutationType is not ("ftms-status-mutation" or "ftms-assignment-mutation")) return;
            var code = payload.RootElement.TryGetProperty("code", out var codeValue) ? codeValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(code)) return;
            var detectedAt = payload.RootElement.TryGetProperty("detectedAt", out var detectedValue) && detectedValue.TryGetInt64(out var parsedAt)
                ? parsedAt : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var actor = payload.RootElement.TryGetProperty("actor", out var actorValue) ? actorValue.GetString() : null;
            var note = payload.RootElement.TryGetProperty("note", out var noteVal) ? noteVal.GetString() : null;
            if (mutationType == "ftms-assignment-mutation")
            {
                if (!payload.RootElement.TryGetProperty("assigneeId", out var assigneeValue) ||
                    !assigneeValue.TryGetInt64(out var assigneeId) || assigneeId <= 0) return;
                var assigneeName = payload.RootElement.TryGetProperty("assigneeName", out var nameVal) ? nameVal.GetString() : null;
                TicketStatus? assignmentStatus = null;
                if (payload.RootElement.TryGetProperty("status", out var stVal) && stVal.TryGetInt32(out var parsedSt) && Enum.IsDefined(typeof(TicketStatus), parsedSt))
                    assignmentStatus = (TicketStatus)parsedSt;
                QueueAssignmentMutationSync(code, assigneeId, assigneeName, actor, assignmentStatus, detectedAt);
                return;
            }
            if (!payload.RootElement.TryGetProperty("status", out var statusValue) ||
                !statusValue.TryGetInt32(out var status) || !Enum.IsDefined(typeof(TicketStatus), status)) return;
            TicketStatus? prevStatus = null;
            if (payload.RootElement.TryGetProperty("previousStatus", out var prevVal) && prevVal.TryGetInt32(out var parsedPrev) && Enum.IsDefined(typeof(TicketStatus), parsedPrev))
                prevStatus = (TicketStatus)parsedPrev;
            QueueStatusMutationSync(code, (TicketStatus)status, prevStatus, actor, note, detectedAt);
        }
        catch (Exception ex) { MonitorText.Text = $"L\u1ed7i \u0111\u1ed3ng b\u1ed9 th\u1eddi gian th\u1ef1c: {ex.Message}"; }
    }

    private void QueueStatusMutationSync(string code, TicketStatus expectedStatus, TicketStatus? previousStatus, string? actor, string? note, long detectedAt)
    {
        QueueMutationSync(async (monitor, cancellationToken) =>
        {
            var mutationTime = DateTimeOffset.FromUnixTimeMilliseconds(detectedAt)
                .ToOffset(TimeSpan.FromHours(7));
            await monitor.ApplyConfirmedStatusMutationAsync(code, expectedStatus, previousStatus,
                actor, cancellationToken, mutationTime, note);
            if (monitor.TryGetTrackedStatus(code, out var status) && status == expectedStatus) return;
            await monitor.SyncUntilStatusAsync(code, expectedStatus, cancellationToken);
            if (monitor.TryGetTrackedStatus(code, out status) && status == expectedStatus) return;
            await Dispatcher.InvokeAsync(() => MonitorText.Text =
                $"\u0110ang ch\u1edd FTMS x\u00e1c nh\u1eadn {code} \u2192 {expectedStatus.DisplayName()} ({mutationTime:HH:mm:ss}).");
        });
    }

    private void QueueAssignmentMutationSync(string code, long expectedAssigneeId, string? expectedAssigneeName, string? actor, TicketStatus? status, long detectedAt)
    {
        QueueMutationSync(async (monitor, cancellationToken) =>
        {
            var mutationTime = DateTimeOffset.FromUnixTimeMilliseconds(detectedAt)
                .ToOffset(TimeSpan.FromHours(7));
            await monitor.ApplyConfirmedAssignmentMutationAsync(code, expectedAssigneeId,
                expectedAssigneeName, actor, status, cancellationToken, mutationTime);
            if (monitor.TryGetTrackedStatus(code, out var curStatus) && (status is null || curStatus == status.Value)) return;
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

    private void OnVisibleFtmsResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
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
        var isListResponse = path.Contains("GetListRequestV12", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("GetListCasesV12", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("GetListAlarm", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("GetListCasesRequest", StringComparison.OrdinalIgnoreCase);
        var isMutationResponse = path.Contains("ChangeStatus", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("UpdateStatus", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("CloseTicket", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("TakeAndAssignment", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("SendMail", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("SendEmail", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("Reply", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("Save", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/Assign", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/Assignment", StringComparison.OrdinalIgnoreCase);
        if (!isListResponse && !isMutationResponse) return;

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
            CurrentUserIdentity? hiddenIdentity;
            try { hiddenIdentity = await _ftmsClient.GetCurrentUserAsync(_lifetime.Token); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }

            if (hiddenIdentity is not null && hiddenIdentity.UserId > 0)
            {
                if (_expectedAccountId is null)
                {
                    _expectedAccountId = hiddenIdentity.UserId;
                    SetSessionStatus("Đã kết nối", "#4AA47B");
                    UpdateDashboard(new DashboardSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, hiddenIdentity));
                    _ = FtmsWebView.ExecuteScriptAsync(
                        $"globalThis.userID = {hiddenIdentity.UserId}; " +
                        $"globalThis.Username = {JsonSerializer.Serialize(hiddenIdentity.UserName)}; " +
                        $"globalThis.UserDept = {hiddenIdentity.DepartmentId?.ToString() ?? "null"}; " +
                        $"globalThis.DepartmentName = {JsonSerializer.Serialize(hiddenIdentity.DepartmentName)};");
                }

                if (_expectedAccountId == hiddenIdentity.UserId)
                {
                    _hiddenReloadAttempts = 0;
                    await StartMonitorOnceAsync(hiddenIdentity.UserId);
                    return;
                }
            }

            if (_expectedAccountId is not null && hiddenIdentity?.UserId != _expectedAccountId)
            {
                if (_hiddenReloadAttempts++ < 2)
                    MonitorWebView.Source = new Uri(FtmsLegacyUrl);
                else
                    MonitorText.Text = "Tài khoản WebView giám sát không khớp; đã tạm dừng để tránh lẫn dữ liệu.";
                return;
            }
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
        if (uri is null || !WebViewLoginRecovery.IsFtmsIhubUri(uri))
        {
            MonitorWebView.Source = new Uri(FtmsLegacyUrl);
            return;
        }

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
            if (_hiddenReloadAttempts++ < 3)
                MonitorWebView.Source = new Uri(FtmsLegacyUrl);
            else
                MonitorText.Text = "Tài khoản WebView giám sát không khớp; đã tạm dừng để tránh lẫn dữ liệu.";
        }
    }

    private async void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _visibleNavigationInProgress = false;
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
                var wasLoggingIn = _wasLoggingIn ||
                    _displayLoginRecovery.State is LoginRecoveryState.FindingLoginMethod
                        or LoginRecoveryState.FollowingSso
                        or LoginRecoveryState.WaitingForUser;
                _wasLoggingIn = false;

                if (wasLoggingIn && (!string.Equals(uri.AbsolutePath.TrimEnd('/'), "/ihub/react/list", StringComparison.OrdinalIgnoreCase) || !uri.Query.Contains("referrer=menu", StringComparison.OrdinalIgnoreCase)))
                {
                    FtmsWebView.Source = new Uri(FtmsReactUrl);
                    return;
                }

                UpdateCurrentPage();
                _manualLoginNotificationShown = false;
                _displayLoginRecovery.NotifyTargetReached();
                _visibleIdentityClient!.NotifyTargetReached();
                var navigationGeneration = _visibleNavigationGeneration;
                CurrentUserIdentity? visibleIdentity = null;
                for (var attempt = 0; attempt < 3 && visibleIdentity is null; attempt++)
                {
                    visibleIdentity = await _visibleIdentityClient.GetCurrentUserAsync(_lifetime.Token);
                    if (visibleIdentity is null && _ftmsClient is not null)
                        visibleIdentity = await _ftmsClient.GetCurrentUserAsync(_lifetime.Token);
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
                _ = FtmsWebView.ExecuteScriptAsync(
                    $"globalThis.userID = {visibleIdentity.UserId}; " +
                    $"globalThis.Username = {JsonSerializer.Serialize(visibleIdentity.UserName)}; " +
                    $"globalThis.UserDept = {visibleIdentity.DepartmentId?.ToString() ?? "null"}; " +
                    $"globalThis.DepartmentName = {JsonSerializer.Serialize(visibleIdentity.DepartmentName)};");
                if (_expectedAccountId != visibleIdentity.UserId)
                {
                    DeactivateAccount();
                    UpdateDashboard(new DashboardSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, visibleIdentity));
                    _expectedAccountId = visibleIdentity.UserId;
                    _hiddenReloadAttempts = 0;
                    if (MonitorWebView.Source is null ||
                        !WebViewLoginRecovery.IsFtmsIhubUri(MonitorWebView.Source))
                        MonitorWebView.Source = new Uri(FtmsLegacyUrl);
                    else
                        _ = CheckAndStartMonitorFromVisibleIdentityAsync(visibleIdentity.UserId);
                }
                return;
            }

            if (WebViewLoginRecovery.IsLoginUri(uri) || WebViewLoginRecovery.IsAdfsUri(uri))
            {
                _wasLoggingIn = true;
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
        _activeUser = null;
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
            var settings = new AppSettings { FtmsUrl = FtmsLegacyUrl, PollIntervalSeconds = 2, IdleDelaySeconds = 0 };
            var telegram = new TelegramOutboxSender(databasePath,
                () => (_settingsStore.Current.TelegramToken, _settingsStore.Current.TelegramChatId), _http,
                getCurrentUserId: () => _activeAccountId);
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
    private void OpenFtms(object sender, RoutedEventArgs e)
    {
        MarkUserActivity();
        if (FtmsWebView.CoreWebView2 is null) return;
        var uri = FtmsWebView.Source;
        if (uri is not null && string.Equals(uri.GetLeftPart(UriPartial.Path).TrimEnd('/'),
                new Uri(FtmsReactUrl).GetLeftPart(UriPartial.Path).TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase))
        {
            MonitorText.Text = $"Tải lại Danh sách hỗ trợ lúc {DateTime.Now:HH:mm:ss}";
            FtmsWebView.Reload();
        }
        else
        {
            MonitorText.Text = $"Quay về Danh sách hỗ trợ lúc {DateTime.Now:HH:mm:ss}";
            FtmsWebView.Source = new Uri(FtmsReactUrl);
        }
    }
    private void ReloadFtms(object sender, RoutedEventArgs e)
    {
        MarkUserActivity();
        var monitor = _monitor;
        var accountLifetime = _accountLifetime;
        if (monitor is not null && accountLifetime is not null && !accountLifetime.IsCancellationRequested)
            _ = Task.Run(() => monitor.SyncNowAsync(forceHistory: true, accountLifetime.Token));

        if (FtmsWebView.CoreWebView2 is null) return;
        MonitorText.Text = $"Tải lại trang hiện tại lúc {DateTime.Now:HH:mm:ss}";
        FtmsWebView.Reload();
    }
    private void OnUserActivity(object sender, InputEventArgs e) => MarkUserActivity();
    private void MarkUserActivity() => _lastUserActivity = DateTimeOffset.UtcNow;
    private bool IsUserBusy() => _settingsOpen || _visibleNavigationInProgress || !_isListPage ||
        DateTimeOffset.UtcNow - _lastUserActivity < TimeSpan.FromSeconds(15);

    private void UpdateCurrentPage()
    {
        if (Uri.TryCreate(FtmsWebView.CoreWebView2?.Source, UriKind.Absolute, out var uri) &&
            WebViewLoginRecovery.IsFtmsIhubUri(uri))
        {
            var path = uri.AbsolutePath.TrimEnd('/');
            _isListPage = string.Equals(path, "/ihub/react/list", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(path, "/ihub/list", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            _isListPage = false;
        }
    }

    private void ApplyRefreshSettings()
    {
        _refreshTimer.Stop(); if (!_settingsStore.Current.AutoRefreshEnabled) return;
        _refreshTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_settingsStore.Current.AutoRefreshSeconds, 5, 3600)); _refreshTimer.Start();
    }

    private async void RunAutoRefresh()
    {
        if (!IsVisible || IsUserBusy() || FtmsWebView.CoreWebView2 is null) return;
        const string script = """
            (() => {
              if (location.hostname.toLowerCase() !== 'ftms.fpt.net') return 'invalid-host';
              if (Date.now() - (window.__ftmsCompanionLastInputAt || 0) < 15000) return 'busy';

              // 1. Kendo Grid refresh (trên trang /ihub/list)
              const kendoRefresh = document.querySelector('a.k-pager-refresh.k-link');
              if (kendoRefresh && kendoRefresh.offsetParent !== null) {
                kendoRefresh.click();
                return 'kendo-btn';
              }
              const kendoGrid = globalThis.jQuery?.('#list-grid').data('kendoGrid');
              if (kendoGrid?.dataSource) {
                kendoGrid.dataSource.read();
                return 'kendo-ds';
              }

              // 2. React Ca vụ/YCHT refresh (trên trang /ihub/react/list)
              const listSearchBtn = document.querySelector('button.absolute.right-1, input[placeholder*="Tìm kiếm" i] ~ button, input[placeholder*="Tìm kiếm" i] + button');
              if (listSearchBtn && listSearchBtn.offsetParent !== null) {
                listSearchBtn.click();
                return 'react-search-btn';
              }

              return 'none';
            })()
            """;
        try
        {
            var raw = await FtmsWebView.ExecuteScriptAsync(script);
            var result = raw?.Trim('"');
            if (result is "kendo-btn" or "kendo-ds" or "react-search-btn")
            {
                MonitorText.Text = $"Tự động làm mới dữ liệu Ca vụ/YCHT lúc {DateTime.Now:HH:mm:ss}";
            }
        }
        catch (Exception ex)
        {
            MonitorText.Text = $"Không thể tự động làm mới: {ex.Message}";
        }
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

    private async Task<TicketActionResult> PauseTicketFromTelegramAsync(string code, CancellationToken cancellationToken)
    {
        var accountId = _activeAccountId;
        var accountLifetime = _accountLifetime;
        var client = _ftmsClient;
        if (accountId is not long activeAccountId || accountLifetime?.IsCancellationRequested != false || client is null)
            return new TicketActionResult(TicketActionStatus.RetryableFailure, "FTMS Companion chưa sẵn sàng.");

        var result = await client.PauseTicketAsync(code, activeAccountId, cancellationToken);
        if (result.IsSuccess && _activeAccountId == activeAccountId && _monitor is not null)
        {
            try
            {
                var userName = _activeUser?.UserName;
                await _monitor.ApplyConfirmedStatusMutationAsync(code, TicketStatus.Paused, TicketStatus.InProgress,
                    userName, accountLifetime.Token, DateTimeOffset.UtcNow, "Hỗ trợ KH");
                await Task.Run(() => _monitor.SyncUntilStatusAsync(code, TicketStatus.Paused, accountLifetime.Token), accountLifetime.Token);
            }
            catch (OperationCanceledException) when (accountLifetime.IsCancellationRequested) { }
            catch (Exception ex) { MonitorText.Text = $"Đã tạm ngưng {code}, nhưng chưa đồng bộ được: {ex.Message}"; }
        }
        return result;
    }

    private Task<TelegramCommandResponse?> HandleTelegramCommandAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith('/'))
            return Task.FromResult<TelegramCommandResponse?>(null);

        var cmd = text.Trim().Split(' ', 2)[0].Split('@', 2)[0].ToLowerInvariant();
        var monitor = _monitor;

        switch (cmd)
        {
            case "/start" or "/help":
            {
                var helpText = new StringBuilder();
                helpText.AppendLine("🤖 <b>TRỢ LÝ BOT FTMS (TOC DATA CENTER)</b>");
                helpText.AppendLine();
                helpText.AppendLine("• <code>/new</code> hoặc <code>/chuanhan</code>: Tra cứu ticket chưa nhận (kèm nút nhận nhanh).");
                helpText.AppendLine("• <code>/my</code> hoặc <code>/cuatoi</code>: Tra cứu ticket đang xử lý của bạn.");
                helpText.AppendLine("• <code>/sla</code>: Tra cứu ticket sắp hoặc đã vi phạm SLA.");
                helpText.AppendLine("• <code>/help</code>: Hướng dẫn các lệnh bot.");
                return Task.FromResult<TelegramCommandResponse?>(new TelegramCommandResponse(helpText.ToString().Trim()));
            }

            case "/new" or "/chuanhan":
            {
                if (monitor is null)
                    return Task.FromResult<TelegramCommandResponse?>(new TelegramCommandResponse("⚠️ <i>Hệ thống giám sát đang khởi động, vui lòng thử lại sau giây lát...</i>"));

                var active = monitor.GetActiveSnapshots();
                var unassigned = active.Where(x => (x.Status is TicketStatus.New or TicketStatus.Assigned) &&
                    (x.AssigneeId is null or 0) &&
                    (string.IsNullOrWhiteSpace(x.AssigneeName) || x.AssigneeName.Trim() == "---" || x.AssigneeName.Trim().Equals("Chưa nhận", StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(x => x.CreatedAt)
                    .ToList();

                if (unassigned.Count == 0)
                {
                    return Task.FromResult<TelegramCommandResponse?>(new TelegramCommandResponse(
                        "✅ <b>Hiện tại không có ticket nào chưa nhận!</b>\nToàn bộ ticket phòng TOC DC đã được phân công/tiếp nhận."));
                }

                var sb = new StringBuilder();
                sb.AppendLine($"📋 <b>DANH SÁCH TICKET CHƯA NHẬN ({unassigned.Count}):</b>");
                sb.AppendLine();

                var buttons = new List<object[]>();
                var maxDisplay = Math.Min(unassigned.Count, 5);
                for (var i = 0; i < maxDisplay; i++)
                {
                    var t = unassigned[i];
                    var waitMinutes = t.CreatedAt is null ? 0 : Math.Max(0, (int)(DateTimeOffset.UtcNow - t.CreatedAt.Value).TotalMinutes);
                    var timeStr = t.CreatedAt is not null
                        ? t.CreatedAt.Value.ToOffset(TimeSpan.FromHours(7)).ToString("HH:mm")
                        : "---";
                    var title = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(t.Title) ? "Không có tiêu đề" : t.Title.Trim());
                    if (title.Length > 45) title = title[..45] + "...";

                    sb.AppendLine($"{i + 1}. 🆔 <code>{t.Code}</code> - <b>{title}</b>");
                    sb.AppendLine($"   ⏰ Tạo: {timeStr} | ⏱ Chờ: {waitMinutes} phút");

                    var route = t.Code.StartsWith("CA", StringComparison.OrdinalIgnoreCase) || t.Code.StartsWith("AL", StringComparison.OrdinalIgnoreCase) ? "case" : "request";
                    var openUrl = $"https://ftms.fpt.net/ihub/{route}/edit/{Uri.EscapeDataString(t.Code)}";
                    buttons.Add([
                        new { text = $"🙋 Nhận {t.Code}", callback_data = $"receive:{t.Code}" },
                        new { text = $"🔎 Mở {t.Code}", url = openUrl }
                    ]);
                }

                if (unassigned.Count > maxDisplay)
                {
                    sb.AppendLine();
                    sb.AppendLine($"<i>... và còn {unassigned.Count - maxDisplay} ticket khác.</i>");
                }

                return Task.FromResult<TelegramCommandResponse?>(new TelegramCommandResponse(sb.ToString().Trim(), buttons.ToArray()));
            }

            case "/my" or "/cuatoi":
            {
                if (monitor is null)
                    return Task.FromResult<TelegramCommandResponse?>(new TelegramCommandResponse("⚠️ <i>Hệ thống giám sát đang khởi động, vui lòng thử lại sau giây lát...</i>"));

                var currentUser = _activeUser;
                if (currentUser is null)
                    return Task.FromResult<TelegramCommandResponse?>(new TelegramCommandResponse("⚠️ <i>Chưa xác định được tài khoản FTMS đăng nhập hiện tại.</i>"));

                var active = monitor.GetActiveSnapshots();
                var personal = active.Where(x => x.AssigneeId == currentUser.UserId)
                    .OrderBy(x => x.Status)
                    .ThenBy(x => x.CreatedAt)
                    .ToList();

                if (personal.Count == 0)
                {
                    return Task.FromResult<TelegramCommandResponse?>(new TelegramCommandResponse(
                        $"✅ <b>Kỹ thuật viên {WebUtility.HtmlEncode(currentUser.UserName ?? "")} hiện không có ticket nào đang xử lý!</b>"));
                }

                var sb = new StringBuilder();
                sb.AppendLine($"👨‍💼 <b>TICKET ĐANG XỬ LÝ - {WebUtility.HtmlEncode(currentUser.UserName ?? "")} ({personal.Count}):</b>");
                sb.AppendLine();

                var buttons = new List<object[]>();
                var maxDisplay = Math.Min(personal.Count, 5);
                for (var i = 0; i < maxDisplay; i++)
                {
                    var t = personal[i];
                    var statusName = t.Status.DisplayName();
                    var title = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(t.Title) ? "Không có tiêu đề" : t.Title.Trim());
                    if (title.Length > 45) title = title[..45] + "...";

                    sb.AppendLine($"{i + 1}. 🆔 <code>{t.Code}</code> [{statusName}]");
                    sb.AppendLine($"   📝 {title}");

                    var route = t.Code.StartsWith("CA", StringComparison.OrdinalIgnoreCase) || t.Code.StartsWith("AL", StringComparison.OrdinalIgnoreCase) ? "case" : "request";
                    var openUrl = $"https://ftms.fpt.net/ihub/{route}/edit/{Uri.EscapeDataString(t.Code)}";
                    var isRq = !t.Code.StartsWith("CA", StringComparison.OrdinalIgnoreCase) && !t.Code.StartsWith("AL", StringComparison.OrdinalIgnoreCase);
                    if (isRq && t.Status == TicketStatus.InProgress)
                    {
                        buttons.Add([new { text = $"🔎 Mở {t.Code}", url = openUrl }, new { text = $"⏸️ Tạm ngưng {t.Code}", callback_data = $"pause:{t.Code}" }]);
                    }
                    else
                    {
                        buttons.Add([new { text = $"🔎 Mở {t.Code}", url = openUrl }]);
                    }
                }

                if (personal.Count > maxDisplay)
                {
                    sb.AppendLine();
                    sb.AppendLine($"<i>... và còn {personal.Count - maxDisplay} ticket khác.</i>");
                }

                return Task.FromResult<TelegramCommandResponse?>(new TelegramCommandResponse(sb.ToString().Trim(), buttons.ToArray()));
            }

            case "/sla":
            {
                if (monitor is null)
                    return Task.FromResult<TelegramCommandResponse?>(new TelegramCommandResponse("⚠️ <i>Hệ thống giám sát đang khởi động, vui lòng thử lại sau giây lát...</i>"));

                var active = monitor.GetActiveSnapshots();
                var slaTickets = active.Where(x => x.SlaType is 2 or 3)
                    .OrderByDescending(x => x.SlaType)
                    .ThenBy(x => x.SlaDeviationMinutes)
                    .ToList();

                if (slaTickets.Count == 0)
                {
                    return Task.FromResult<TelegramCommandResponse?>(new TelegramCommandResponse(
                        "✅ <b>Tuyệt vời! Hiện tại phòng TOC không có ticket nào vi phạm hoặc sắp vi phạm SLA.</b>"));
                }

                var sb = new StringBuilder();
                sb.AppendLine($"🚨 <b>CẢNH BÁO SLA PHÒNG TOC ({slaTickets.Count}):</b>");
                sb.AppendLine();

                var buttons = new List<object[]>();
                var maxDisplay = Math.Min(slaTickets.Count, 5);
                for (var i = 0; i < maxDisplay; i++)
                {
                    var t = slaTickets[i];
                    var slaLabel = t.SlaType == 3 ? "🔴 ĐÃ VI PHẠM" : "🟠 SẮP VI PHẠM";
                    var slaTime = t.SlaDeviationMinutes is not null
                        ? (t.SlaType == 3 ? $"{Math.Abs(t.SlaDeviationMinutes.Value)} phút" : $"còn {t.SlaDeviationMinutes.Value} phút")
                        : "";
                    var assignee = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(t.AssigneeName) ? "Chưa nhận" : t.AssigneeName.Trim());

                    sb.AppendLine($"{i + 1}. 🆔 <code>{t.Code}</code> - {slaLabel} ({slaTime})");
                    sb.AppendLine($"   👨‍💼 Xử lý: {assignee}");

                    var route = t.Code.StartsWith("CA", StringComparison.OrdinalIgnoreCase) || t.Code.StartsWith("AL", StringComparison.OrdinalIgnoreCase) ? "case" : "request";
                    var openUrl = $"https://ftms.fpt.net/ihub/{route}/edit/{Uri.EscapeDataString(t.Code)}";
                    buttons.Add([new { text = $"🔎 Mở {t.Code}", url = openUrl }]);
                }

                return Task.FromResult<TelegramCommandResponse?>(new TelegramCommandResponse(sb.ToString().Trim(), buttons.ToArray()));
            }

            default:
                return Task.FromResult<TelegramCommandResponse?>(null);
        }
    }

    private void UpdateDashboard(DashboardSummary s)
    {
        _activeUser = s.CurrentUser;
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
