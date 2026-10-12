using System.Text.Json;
using Microsoft.Web.WebView2.Wpf;

namespace FTMS.Desktop;

internal enum LoginRecoveryState
{
    Idle,
    Navigating,
    FindingLoginMethod,
    FollowingSso,
    WaitingForUser
}

internal sealed record LoginRecoveryStatus(LoginRecoveryState State, string Message, bool RequiresUserAction = false);

internal sealed class WebViewLoginRecovery(WebView2 webView, Uri targetUri)
{
    private static readonly TimeSpan[] LoginMethodRetryDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4)
    ];

    private readonly SemaphoreSlim _recoveryLock = new(1, 1);
    private int _generation;
    private bool _loginMethodClicked;
    private bool _waitingForUser;
    private DateTimeOffset _nextNavigationAllowedAt = DateTimeOffset.MinValue;

    public event Action<LoginRecoveryStatus>? StatusChanged;

    public LoginRecoveryState State { get; private set; }

    public async Task BeginAsync(CancellationToken cancellationToken)
    {
        if (_waitingForUser || !await _recoveryLock.WaitAsync(0, cancellationToken)) return;
        try
        {
            var generation = _generation;
            var currentUri = await GetCurrentUriAsync(cancellationToken);
            if (currentUri is null || generation != _generation) return;

            if (IsLoginUri(currentUri) || await IsLoginDocumentAsync(cancellationToken))
            {
                await SelectFptCorporationAsync(generation, cancellationToken);
                return;
            }

            if (IsAdfsUri(currentUri) || State == LoginRecoveryState.FollowingSso)
            {
                await WaitForSsoAsync(generation, cancellationToken);
                return;
            }

            await NavigateToTargetAsync(cancellationToken);
        }
        finally
        {
            _recoveryLock.Release();
        }
    }

    public void NotifyTargetReached()
    {
        _generation++;
        _loginMethodClicked = false;
        _waitingForUser = false;
        State = LoginRecoveryState.Idle;
    }

    private async Task SelectFptCorporationAsync(int generation, CancellationToken cancellationToken)
    {
        if (_loginMethodClicked)
        {
            await WaitForSsoAsync(generation, cancellationToken);
            return;
        }

        Publish(LoginRecoveryState.FindingLoginMethod, "Đang chọn phương thức FPT Corporation");
        foreach (var delay in LoginMethodRetryDelays)
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            if (generation != _generation) return;

            var currentUri = await GetCurrentUriAsync(cancellationToken);
            if (currentUri is null ||
                (!IsLoginUri(currentUri) &&
                 !await IsLoginDocumentAsync(cancellationToken))) return;

            var result = await ExecuteScriptAsync(LoginMethodScript, cancellationToken);
            if (generation != _generation) return;
            if (result is "clicked" or "already-clicked")
            {
                _loginMethodClicked = true;
                await WaitForSsoAsync(generation, cancellationToken);
                return;
            }

            if (result == "ambiguous")
            {
                EnterWaitingForUser("Không thể xác định an toàn nút FPT Corporation. Vui lòng đăng nhập thủ công.");
                return;
            }
        }

        EnterWaitingForUser("Chưa tìm thấy nút FPT Corporation. Vui lòng đăng nhập thủ công.");
    }

    private async Task WaitForSsoAsync(int generation, CancellationToken cancellationToken)
    {
        Publish(LoginRecoveryState.FollowingSso, "Đang chờ FPT Corporation SSO hoàn tất");
        var expiresAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTimeOffset.UtcNow < expiresAt)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            if (generation != _generation) return;

            var currentUri = await GetCurrentUriAsync(cancellationToken);
            if (currentUri is null) return;
            if (IsFtmsIhubUri(currentUri) &&
                !await IsLoginDocumentAsync(cancellationToken)) return;
        }

        if (generation == _generation)
            EnterWaitingForUser("SSO/ADFS cần bạn hoàn tất đăng nhập hoặc MFA trong cửa sổ FTMS.");
    }

    private async Task NavigateToTargetAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow < _nextNavigationAllowedAt) return;
        _nextNavigationAllowedAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(12);
        Publish(LoginRecoveryState.Navigating, "Phiên FTMS đã hết hạn, đang mở lại trang đăng nhập");
        await webView.Dispatcher.InvokeAsync(() => webView.Source = targetUri).Task.WaitAsync(cancellationToken);
    }

    private void EnterWaitingForUser(string message)
    {
        _waitingForUser = true;
        Publish(LoginRecoveryState.WaitingForUser, message, true);
    }

    private void Publish(LoginRecoveryState state, string message, bool requiresUserAction = false)
    {
        State = state;
        StatusChanged?.Invoke(new LoginRecoveryStatus(state, message, requiresUserAction));
    }

    private async Task<Uri?> GetCurrentUriAsync(CancellationToken cancellationToken) =>
        await webView.Dispatcher.InvokeAsync(() => webView.Source).Task.WaitAsync(cancellationToken);

    internal async Task<bool> IsLoginDocumentAsync(CancellationToken cancellationToken) =>
        string.Equals(await ExecuteScriptAsync(LoginDocumentScript, cancellationToken), "login", StringComparison.Ordinal);

    private async Task<string> ExecuteScriptAsync(string script, CancellationToken cancellationToken)
    {
        var operation = await webView.Dispatcher.InvokeAsync(() => webView.ExecuteScriptAsync(script)).Task.WaitAsync(cancellationToken);
        var raw = await operation.WaitAsync(cancellationToken);
        try { return JsonSerializer.Deserialize<string>(raw) ?? string.Empty; }
        catch (JsonException) { return raw.Trim('"'); }
    }

    public bool IsTargetUri(Uri uri) =>
        string.Equals(uri.Scheme, targetUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.Host, targetUri.Host, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.AbsolutePath.TrimEnd('/'), targetUri.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.Query, targetUri.Query, StringComparison.OrdinalIgnoreCase);

    public static bool IsLoginUri(Uri uri) => IsFtmsHost(uri) &&
        uri.AbsolutePath.StartsWith("/id/login", StringComparison.OrdinalIgnoreCase);

    public static bool IsAdfsUri(Uri uri) =>
        uri.AbsolutePath.Contains("/adfs/", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Contains("adfs", StringComparison.OrdinalIgnoreCase);

    public static bool IsFtmsIhubUri(Uri uri) => IsFtmsHost(uri) &&
        uri.AbsolutePath.StartsWith("/ihub/", StringComparison.OrdinalIgnoreCase);

    private static bool IsFtmsHost(Uri uri) =>
        string.Equals(uri.Host, "ftms.fpt.net", StringComparison.OrdinalIgnoreCase);

    private const string LoginDocumentScript = """
        (() => {
          const title = String(document.title || '');
          const hasLoginMethod = Boolean(document.querySelector('.bg-btn.login-method'));
          return hasLoginMethod || /đăng nhập\s*-\s*FTI Management System/i.test(title)
            ? 'login'
            : 'other';
        })()
        """;

    internal const string LoginMethodScript = """
        (() => {
          const normalize = value => String(value || '').replace(/\s+/g, ' ').trim();
          const matches = Array.from(document.querySelectorAll('.bg-btn.login-method')).filter(candidate => {
            const labels = Array.from(candidate.querySelectorAll('p')).map(p => normalize(p.textContent));
            if (labels.some(label => /(^|\s)OTP($|\s)|mật khẩu một lần/i.test(label))) return false;
            const imageMatches = Array.from(candidate.querySelectorAll('img')).some(image =>
              String(image.getAttribute('src') || image.src || '').toLowerCase().includes('login_adfs.png'));
            return imageMatches || labels.some(label => label === 'FPT Corporation');
          });
          if (matches.length > 1) return 'ambiguous';
          if (matches.length === 0) return 'not-found';
          const target = matches[0];
          if (target.dataset.ftmsCompanionLoginClicked === 'true') return 'already-clicked';
          target.dataset.ftmsCompanionLoginClicked = 'true';
          target.click();
          return 'clicked';
        })()
        """;
}
