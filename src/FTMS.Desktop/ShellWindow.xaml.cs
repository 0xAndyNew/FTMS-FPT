using System.IO;
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

public partial class ShellWindow : Window
{
    private const string FtmsUrl = "https://ftms.fpt.net/ihub/react/list?referrer=menu";
    private readonly SettingsStore _settingsStore = new();
    private readonly HttpClient _telegramHttp;
    private CancellationTokenSource _lifetime = new();
    private TicketMonitor? _monitor;
    private bool _monitorStarted;
    private readonly DispatcherTimer _refreshTimer = new();
    private DateTimeOffset _lastUserActivity = DateTimeOffset.MinValue;

    public ShellWindow()
    {
        InitializeComponent(); _settingsStore.Load(); _telegramHttp = TelegramHttpClientFactory.Create(() => _settingsStore.Current);
        Loaded += InitializeAsync; Closed += (_, _) => { _lifetime.Cancel(); _refreshTimer.Stop(); _telegramHttp.Dispose(); };
        _refreshTimer.Tick += (_, _) => RunAutoRefresh();
    }

    private async void InitializeAsync(object sender, RoutedEventArgs e)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FTMS.Companion");
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(root, "WebView2"));
        await FtmsWebView.EnsureCoreWebView2Async(environment);
        FtmsWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        await FtmsWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ApiObserverScript);
        FtmsWebView.Source = new Uri(FtmsUrl);

        var settings = new AppSettings { FtmsUrl = FtmsUrl, PollIntervalSeconds = 10, IdleDelaySeconds = 0, AutoRefreshEnabled = true };
        var databasePath = Path.Combine(root, "ftms.db");
        var client = new WebViewFtmsClient(FtmsWebView, FtmsUrl);
        var telegram = new TelegramOutboxSender(databasePath,
            () => (_settingsStore.Current.TelegramToken, _settingsStore.Current.TelegramChatId), _telegramHttp);
        _monitor = new TicketMonitor(client, new SqliteTicketStore(databasePath), telegram, new TicketChangeDetector(), settings);
        _monitor.StatusChanged += message => Dispatcher.Invoke(() => MonitorText.Text = message);
        _monitor.SummaryChanged += summary => Dispatcher.Invoke(() => UpdateDashboard(summary));
        await _monitor.InitializeAsync(_lifetime.Token);
        ApplyRefreshSettings();
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            string? message;
            try { message = JsonSerializer.Deserialize<string>(e.WebMessageAsJson); }
            catch (JsonException) { return; }
            if (message == "ftms-api-updated" && _monitor is not null)
            {
                MonitorText.Text = "API co du lieu moi, dang xu ly ngay";
                await _monitor.SyncNowAsync(_lifetime.Token);
            }
        }
        catch (Exception ex) { MonitorText.Text = $"Loi realtime: {ex.Message}"; }
    }

    private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        var url = FtmsWebView.Source?.AbsoluteUri ?? "";
        var login = url.Contains("/id/login", StringComparison.OrdinalIgnoreCase) || url.Contains("/adfs/", StringComparison.OrdinalIgnoreCase);
        SessionText.Text = login ? "Dang dang nhap" : url.Contains("/ihub/", StringComparison.OrdinalIgnoreCase) ? "Da ket noi" : "Dang ket noi";
        StatusDot.Fill = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(login ? "#D9A441" : "#4AA47B"));
        if (!login && url.Contains("/ihub/", StringComparison.OrdinalIgnoreCase) && !_monitorStarted && _monitor is not null)
        {
            _monitorStarted = true;
            _ = _monitor.RunAsync(_lifetime.Token);
        }
    }

    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        if (new SettingsWindow(_settingsStore) { Owner = this }.ShowDialog() == true) ApplyRefreshSettings();
    }
    private void OpenFtms(object sender, RoutedEventArgs e) => FtmsWebView.Source = new Uri(FtmsUrl);
    private void RecoverLogin(object sender, RoutedEventArgs e) => FtmsWebView.Source = new Uri(FtmsUrl);
    private void ReloadFtms(object sender, RoutedEventArgs e) => FtmsWebView.Reload();
    private void OnUserActivity(object sender, InputEventArgs e) => _lastUserActivity = DateTimeOffset.Now;

    private void ApplyRefreshSettings()
    {
        _refreshTimer.Stop();
        if (!_settingsStore.Current.AutoRefreshEnabled) return;
        _refreshTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_settingsStore.Current.AutoRefreshSeconds, 5, 3600));
        _refreshTimer.Start();
    }

    private void RunAutoRefresh()
    {
        if (DateTimeOffset.Now - _lastUserActivity < TimeSpan.FromSeconds(8))
        {
            MonitorText.Text = "Auto refresh tam dung vi ban dang thao tac";
            return;
        }
        if (FtmsWebView.CoreWebView2 is null) return;
        MonitorText.Text = $"Auto refresh luc {DateTime.Now:HH:mm:ss}";
        FtmsWebView.Reload();
    }

    private void UpdateDashboard(DashboardSummary summary)
    {
        TotalValue.Text = summary.Total.ToString("N0");
        NewValue.Text = (summary.New + summary.Assigned).ToString("N0");
        InProgressValue.Text = summary.InProgress.ToString("N0");
        PausedValue.Text = summary.Paused.ToString("N0");
        ClosedValue.Text = (summary.Completed + summary.Closed).ToString("N0");
        SlaValue.Text = (summary.SlaRisk + summary.SlaViolated).ToString("N0");
    }

    private const string ApiObserverScript = """
        (() => {
          if (window.__ftmsCompanionInstalled) return; window.__ftmsCompanionInstalled = true;
          const watched = ['GetListRequestV12','GetListCasesV12','GetListAlarm','GetListCasesRequest'];
          const isWatched = url => watched.some(x => String(url || '').includes(x));
          const originalFetch = window.fetch;
          window.fetch = async (...args) => { const response = await originalFetch(...args); if (isWatched(args[0]?.url || args[0])) window.chrome.webview.postMessage('ftms-api-updated'); return response; };
          const open = XMLHttpRequest.prototype.open; const send = XMLHttpRequest.prototype.send;
          XMLHttpRequest.prototype.open = function(method, url, ...rest) { this.__ftmsUrl = url; return open.call(this, method, url, ...rest); };
          XMLHttpRequest.prototype.send = function(...args) { this.addEventListener('load', () => { if (isWatched(this.__ftmsUrl)) window.chrome.webview.postMessage('ftms-api-updated'); }); return send.apply(this, args); };
        })();
        """;
}
