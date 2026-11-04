using System.Windows;

namespace FTMS.Desktop;

public partial class SettingsWindow : Window
{
    private readonly SettingsStore _store;
    public SettingsWindow(SettingsStore store)
    {
        InitializeComponent(); _store = store;
        TokenBox.Password = store.Current.TelegramToken; ChatIdBox.Text = store.Current.TelegramChatId; StartupCheck.IsChecked = store.Current.StartWithWindows;
        ProxyUrlBox.Text = store.Current.TelegramProxyUrl;
        AutoRefreshCheck.IsChecked = store.Current.AutoRefreshEnabled; RefreshSecondsBox.Text = store.Current.AutoRefreshSeconds.ToString();
    }
    private void Save(object sender, RoutedEventArgs e)
    {
        var proxyUrl = ProxyUrlBox.Text.Trim();
        if (!TelegramHttpClientFactory.TryParseProxy(proxyUrl, out _))
        {
            System.Windows.MessageBox.Show(this, "HTTP proxy phải có dạng http://host:port (không chứa tài khoản hoặc đường dẫn).", "Proxy Telegram",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var text = new string((RefreshSecondsBox.Text ?? "").Where(char.IsDigit).ToArray());
        var seconds = int.TryParse(text, out var value) ? Math.Clamp(value, 1, 3600) : 30;
        _store.Save(new DesktopSettings(TokenBox.Password.Trim(), ChatIdBox.Text.Trim(), StartupCheck.IsChecked == true,
            AutoRefreshCheck.IsChecked == true, seconds, proxyUrl));
        DialogResult = true;
    }
    private void Cancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
