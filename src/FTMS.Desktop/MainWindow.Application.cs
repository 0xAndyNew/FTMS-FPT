using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using FTMS.Application;
using FTMS.Domain;
using FTMS.Infrastructure;
using Microsoft.Web.WebView2.Core;

namespace FTMS.Desktop;

public partial class MainWindow
{
    private const string FtmsUrl = "https://ftms.fpt.net/ihub/react/list?referrer=menu";
    private CancellationTokenSource? _monitorCancellation;
    private DateTimeOffset _lastUserActivity = DateTimeOffset.MinValue;

    private void ConfigureApplication()
    {
        Loaded += InitializeBrowserAsync;
        Closed += (_, _) => _monitorCancellation?.Cancel();
    }

    private async void InitializeBrowserAsync(object sender, RoutedEventArgs e)
    {
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FTMS.Companion", "WebView2");
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
        await FtmsWebView.EnsureCoreWebView2Async(environment);
        FtmsWebView.Source = new Uri(FtmsUrl);
        MonitorStatusText.Text = "San sang. Dang nhap FTMS roi bat theo doi.";
    }

    private async void StartMonitoring(object sender, RoutedEventArgs e)
    {
        _monitorCancellation?.Cancel();
        _monitorCancellation = new CancellationTokenSource();
        var settings = ReadSettings();
        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FTMS.Companion");
        var databasePath = Path.Combine(dataDirectory, "ftms.db");
        var client = new WebViewFtmsClient(FtmsWebView, settings.FtmsUrl);
        var store = new SqliteTicketStore(databasePath);
        var telegram = new TelegramOutboxSender(databasePath, () => (settings.TelegramBotToken, settings.TelegramChatId), new HttpClient());
        var monitor = new TicketMonitor(client, store, telegram, new TicketChangeDetector(), settings);
        monitor.StatusChanged += message => Dispatcher.Invoke(() => MonitorStatusText.Text = message);
        try
        {
            await monitor.InitializeAsync(_monitorCancellation.Token);
            _ = monitor.RunAsync(_monitorCancellation.Token);
            MonitorStatusText.Text = "Da bat theo doi";
        }
        catch (Exception ex) { MonitorStatusText.Text = $"Khong the khoi dong: {ex.Message}"; }
    }

    private void StopMonitoring(object sender, RoutedEventArgs e)
    {
        _monitorCancellation?.Cancel();
        MonitorStatusText.Text = "Da dung theo doi";
    }

    private AppSettings ReadSettings() => new()
    {
        FtmsUrl = FtmsUrl,
        PollIntervalSeconds = int.TryParse(PollIntervalText.Text, out var seconds) ? Math.Max(5, seconds) : 30,
        AutoRefreshEnabled = AutoRefreshCheck.IsChecked == true,
        TelegramBotToken = TelegramTokenText.Password.Trim(),
        TelegramChatId = TelegramChatText.Text.Trim()
    };

    private void OnUserActivity(object sender, InputEventArgs e) => _lastUserActivity = DateTimeOffset.Now;
    private void OpenFtms(object sender, RoutedEventArgs e) => FtmsWebView.Source = new Uri(FtmsUrl);
    private void RecoverLogin(object sender, RoutedEventArgs e) => FtmsWebView.Source = new Uri(FtmsUrl);
    private void ReloadFtms(object sender, RoutedEventArgs e) => FtmsWebView.Reload();

    private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        var url = FtmsWebView.Source?.AbsoluteUri ?? string.Empty;
        var login = url.Contains("/id/login", StringComparison.OrdinalIgnoreCase) || url.Contains("/adfs/", StringComparison.OrdinalIgnoreCase);
        SessionStatusText.Text = login ? "Session: dang dang nhap lai" : url.Contains("/ihub/", StringComparison.OrdinalIgnoreCase) ? "Session: da dang nhap" : "Session: chua xac dinh";
    }
}
