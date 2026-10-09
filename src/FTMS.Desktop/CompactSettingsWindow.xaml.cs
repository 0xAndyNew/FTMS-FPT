using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using FTMS.Infrastructure;

namespace FTMS.Desktop;

public partial class CompactSettingsWindow : Window
{
    private readonly SettingsStore _store;

    public CompactSettingsWindow(SettingsStore store)
    {
        InitializeComponent();
        _store = store;
        TokenBox.Password = store.Current.TelegramToken;
        ChatIdBox.Text = store.Current.TelegramChatId;
        ProxyUrlBox.Text = store.Current.TelegramProxyUrl;
        StartupCheck.IsChecked = store.Current.StartWithWindows;
        AutoRefreshCheck.IsChecked = store.Current.AutoRefreshEnabled;
        RefreshSecondsBox.Text = store.Current.AutoRefreshSeconds.ToString();
    }

    private void Save(object sender, RoutedEventArgs e)
    {
        var proxyUrl = ProxyUrlBox.Text.Trim();
        if (!TelegramHttpClientFactory.TryParseProxy(proxyUrl, out _))
        {
            ShowTestStatus("HTTP proxy phải có dạng http://host:port (không chứa tài khoản hoặc đường dẫn).", false);
            return;
        }
        var seconds = int.TryParse(RefreshSecondsBox.Text, out var value) ? Math.Clamp(value, 5, 3600) : 30;
        _store.Save(new DesktopSettings(TokenBox.Password.Trim(), ChatIdBox.Text.Trim(), StartupCheck.IsChecked == true,
            AutoRefreshCheck.IsChecked == true, seconds, proxyUrl));
        DialogResult = true;
    }

    private async void TestTelegram(object sender, RoutedEventArgs e)
    {
        var token = TokenBox.Password.Trim();
        var chatId = ChatIdBox.Text.Trim();
        var proxyUrl = ProxyUrlBox.Text.Trim();
        if (!TelegramHttpClientFactory.TryParseProxy(proxyUrl, out _))
        {
            ShowTestStatus("HTTP proxy phải có dạng http://host:port (không chứa tài khoản hoặc đường dẫn).", false);
            return;
        }
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId))
        {
            ShowTestStatus("Vui l\u00f2ng nh\u1eadp \u0111\u1ea7y \u0111\u1ee7 bot token v\u00e0 chat ID.", false);
            return;
        }

        TestTelegramButton.IsEnabled = false;
        ShowTestStatus("\u0110ang g\u1eedi tin nh\u1eafn th\u1eed...", true);
        try
        {
            using var http = TelegramHttpClientFactory.Create(() => new DesktopSettings(TelegramProxyUrl: proxyUrl));
            http.Timeout = TimeSpan.FromSeconds(15);
            var response = await http.PostAsJsonAsync($"https://api.telegram.org/bot{token}/sendMessage", new
            {
                chat_id = chatId,
                text = $"FTMS Companion: K\u1ebft n\u1ed1i Telegram th\u00e0nh c\u00f4ng l\u00fac {DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)):HH:mm:ss dd/MM/yyyy}.",
                disable_web_page_preview = true
            });
            if (response.IsSuccessStatusCode)
            {
                ShowTestStatus("\u0110\u00e3 g\u1eedi tin nh\u1eafn th\u1eed th\u00e0nh c\u00f4ng.", true);
                return;
            }

            var body = await response.Content.ReadAsStringAsync();
            ShowTestStatus($"G\u1eedi th\u1eed th\u1ea5t b\u1ea1i ({(int)response.StatusCode}): " +
                TelegramErrorSanitizer.Sanitize(TryGetTelegramError(body), token), false);
        }
        catch (Exception ex)
        {
            ShowTestStatus($"Kh\u00f4ng th\u1ec3 k\u1ebft n\u1ed1i Telegram: {TelegramErrorSanitizer.Sanitize(ex.Message, token)}", false);
        }
        finally { TestTelegramButton.IsEnabled = true; }
    }

    private void ShowTestStatus(string message, bool success)
    {
        TestStatusPanel.Visibility = Visibility.Visible;
        TestStatusPanel.Background = Brush(success ? "#EDF6F0" : "#FDEBEC");
        TestStatusText.Foreground = Brush(success ? "#347153" : "#9B3434");
        TestStatusText.Text = message;
    }

    private static SolidColorBrush Brush(string color) => new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));

    private static string TryGetTelegramError(string responseBody)
    {
        try
        {
            using var json = JsonDocument.Parse(responseBody);
            return json.RootElement.TryGetProperty("description", out var description)
                ? description.GetString() ?? "Telegram kh\u00f4ng cung c\u1ea5p chi ti\u1ebft"
                : "Telegram kh\u00f4ng cung c\u1ea5p chi ti\u1ebft";
        }
        catch (JsonException) { return "Ph\u1ea3n h\u1ed3i Telegram kh\u00f4ng h\u1ee3p l\u1ec7"; }
    }

    private void Cancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
