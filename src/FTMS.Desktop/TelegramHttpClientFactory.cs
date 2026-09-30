using System.Net.Http;
using FTMS.Infrastructure;

namespace FTMS.Desktop;

internal static class TelegramHttpClientFactory
{
    public static HttpClient Create(Func<DesktopSettings> settings) => new(new HttpClientHandler
    {
        UseProxy = true,
        Proxy = new TelegramHttpProxy(() => settings().TelegramProxyUrl)
    })
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    public static bool TryParseProxy(string? value, out Uri? proxy) =>
        TelegramHttpProxy.TryParse(value, out proxy);
}
