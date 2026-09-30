using System.Net;
using System.Text;
using System.Text.Json;
using FTMS.Domain;
using FTMS.Infrastructure;
using Xunit;

namespace FTMS.Companion.Tests;

public sealed class TelegramCallbackReceiverTests
{
    [Fact]
    public async Task RetryableClaimDoesNotCommitOffsetOrProcessLaterUpdate()
    {
        using var directory = new TemporaryDirectory();
        var handler = new TelegramHandler(Updates(10, "RQ-1", 11, "RQ-2"));
        using var http = new HttpClient(handler);
        var claimed = new List<string>();
        var receiver = new TelegramCallbackReceiver(directory.Path, () => ("123:token", "456"), http,
            (code, _) =>
            {
                claimed.Add(code);
                return Task.FromResult(new TicketClaimResult(TicketClaimStatus.RetryableFailure, "temporary"));
            });

        await receiver.CheckAsync(CancellationToken.None);

        Assert.Single(claimed);
        Assert.Equal("RQ-1", claimed[0]);
        Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, "telegram-123.offset")));
    }

    [Fact]
    public async Task SuccessfulClaimCommitsBotScopedOffset()
    {
        using var directory = new TemporaryDirectory();
        var handler = new TelegramHandler(Updates(10, "RQ-1"));
        using var http = new HttpClient(handler);
        var receiver = new TelegramCallbackReceiver(directory.Path, () => ("123:token", "456"), http,
            (_, _) => Task.FromResult(new TicketClaimResult(TicketClaimStatus.Claimed, "ok")));

        await receiver.CheckAsync(CancellationToken.None);

        var offset = await File.ReadAllTextAsync(System.IO.Path.Combine(directory.Path, "telegram-123.offset"));
        Assert.Equal("11", offset);
    }

    [Fact]
    public async Task AuthenticationFailureIsRetriedWithoutCommittingOffset()
    {
        using var directory = new TemporaryDirectory();
        var handler = new TelegramHandler(Updates(10, "RQ-1"));
        using var http = new HttpClient(handler);
        var receiver = new TelegramCallbackReceiver(directory.Path, () => ("123:token", "456"), http,
            (_, _) => Task.FromResult(new TicketClaimResult(TicketClaimStatus.AuthenticationRequired, "login")));

        await receiver.CheckAsync(CancellationToken.None);

        Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, "telegram-123.offset")));
    }

    [Fact]
    public async Task DifferentBotsUseDifferentOffsets()
    {
        using var directory = new TemporaryDirectory();
        var handler = new TelegramHandler(Updates(10, "RQ-1"));
        using var http = new HttpClient(handler);
        var token = "123:token";
        var receiver = new TelegramCallbackReceiver(directory.Path, () => (token, "456"), http,
            (_, _) => Task.FromResult(new TicketClaimResult(TicketClaimStatus.Claimed, "ok")));

        await receiver.CheckAsync(CancellationToken.None);
        token = "999:token";
        await receiver.CheckAsync(CancellationToken.None);

        Assert.True(File.Exists(System.IO.Path.Combine(directory.Path, "telegram-123.offset")));
        Assert.True(File.Exists(System.IO.Path.Combine(directory.Path, "telegram-999.offset")));
    }

    [Fact]
    public async Task LongPollingPassesConfiguredTimeoutSeconds()
    {
        using var directory = new TemporaryDirectory();
        Uri? requestedUri = null;
        var handler = new TelegramHandler(Updates(10, "RQ-1"), req => requestedUri ??= req.RequestUri);
        using var http = new HttpClient(handler);
        var receiver = new TelegramCallbackReceiver(directory.Path, () => ("123:token", "456"), http,
            (_, _) => Task.FromResult(new TicketClaimResult(TicketClaimStatus.Claimed, "ok")));

        await receiver.CheckAsync(CancellationToken.None, timeoutSeconds: 25);

        Assert.NotNull(requestedUri);
        Assert.Contains("timeout=25", requestedUri.Query);
    }

    private static string Updates(params object[] values)
    {
        var updates = new List<object>();
        for (var index = 0; index < values.Length; index += 2)
        {
            var updateId = (int)values[index];
            var code = (string)values[index + 1];
            updates.Add(new
            {
                update_id = updateId,
                callback_query = new
                {
                    id = $"callback-{updateId}",
                    data = $"receive:{code}",
                    message = new { message_id = updateId, chat = new { id = 456 } }
                }
            });
        }
        return JsonSerializer.Serialize(new { ok = true, result = updates });
    }

    private sealed class TelegramHandler(string updates, Action<HttpRequestMessage>? onRequest = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onRequest?.Invoke(request);
            var json = request.Method == HttpMethod.Get ? updates : "{\"ok\":true,\"result\":true}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ftms-tests-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
