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

    [Fact]
    public async Task MessageCommand_AuthorizedChat_InvokesCommandHandlerAndCommitsOffset()
    {
        using var directory = new TemporaryDirectory();
        var sentRequests = new List<HttpRequestMessage>();
        var handler = new TelegramHandler(MessageUpdates(10, 456, "/new"), req => sentRequests.Add(req));
        using var http = new HttpClient(handler);
        var handledCommand = "";
        var receiver = new TelegramCallbackReceiver(directory.Path, () => ("123:token", "456"), http,
            (_, _) => Task.FromResult(new TicketClaimResult(TicketClaimStatus.Claimed, "ok")),
            (cmd, _) =>
            {
                handledCommand = cmd;
                return Task.FromResult<TelegramCommandResponse?>(new TelegramCommandResponse("DANH SACH TICKET"));
            });

        await receiver.CheckAsync(CancellationToken.None);

        Assert.Equal("/new", handledCommand);
        Assert.Contains(sentRequests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("sendMessage"));
        var offset = await File.ReadAllTextAsync(Path.Combine(directory.Path, "telegram-123.offset"));
        Assert.Equal("11", offset);
    }

    [Fact]
    public async Task MessageCommand_UnauthorizedChat_IgnoredAndCommitsOffset()
    {
        using var directory = new TemporaryDirectory();
        var sentRequests = new List<HttpRequestMessage>();
        var handler = new TelegramHandler(MessageUpdates(10, 999, "/new"), req => sentRequests.Add(req));
        using var http = new HttpClient(handler);
        var commandInvoked = false;
        var receiver = new TelegramCallbackReceiver(directory.Path, () => ("123:token", "456"), http,
            (_, _) => Task.FromResult(new TicketClaimResult(TicketClaimStatus.Claimed, "ok")),
            (cmd, _) =>
            {
                commandInvoked = true;
                return Task.FromResult<TelegramCommandResponse?>(new TelegramCommandResponse("OK"));
            });

        await receiver.CheckAsync(CancellationToken.None);

        Assert.False(commandInvoked);
        Assert.DoesNotContain(sentRequests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("sendMessage"));
        var offset = await File.ReadAllTextAsync(Path.Combine(directory.Path, "telegram-123.offset"));
        Assert.Equal("11", offset);
    }

    [Fact]
    public async Task MessageCommand_NullResponse_DoesNotSendAndCommitsOffset()
    {
        using var directory = new TemporaryDirectory();
        var sentRequests = new List<HttpRequestMessage>();
        var handler = new TelegramHandler(MessageUpdates(10, 456, "tin nhan thuong"), req => sentRequests.Add(req));
        using var http = new HttpClient(handler);
        var receiver = new TelegramCallbackReceiver(directory.Path, () => ("123:token", "456"), http,
            (_, _) => Task.FromResult(new TicketClaimResult(TicketClaimStatus.Claimed, "ok")),
            (_, _) => Task.FromResult<TelegramCommandResponse?>(null));

        await receiver.CheckAsync(CancellationToken.None);

        Assert.DoesNotContain(sentRequests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("sendMessage"));
        var offset = await File.ReadAllTextAsync(Path.Combine(directory.Path, "telegram-123.offset"));
        Assert.Equal("11", offset);
    }

    [Fact]
    public async Task SuccessfulPause_CommitsOffsetAndEditsMarkup()
    {
        using var directory = new TemporaryDirectory();
        var sentRequests = new List<HttpRequestMessage>();
        var handler = new TelegramHandler(PauseUpdates(10, 456, "RQ-1"), req => sentRequests.Add(req));
        using var http = new HttpClient(handler);
        var pausedCode = string.Empty;
        var receiver = new TelegramCallbackReceiver(directory.Path, () => ("123:token", "456"), http,
            (_, _) => Task.FromResult(new TicketClaimResult(TicketClaimStatus.Claimed, "ok")),
            (code, _) =>
            {
                pausedCode = code;
                return Task.FromResult(new TicketActionResult(TicketActionStatus.Success, "ok"));
            });

        await receiver.CheckAsync(CancellationToken.None);

        Assert.Equal("RQ-1", pausedCode);
        Assert.Contains(sentRequests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("editMessageReplyMarkup"));
        Assert.Contains(sentRequests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("answerCallbackQuery"));
        var offset = await File.ReadAllTextAsync(Path.Combine(directory.Path, "telegram-123.offset"));
        Assert.Equal("11", offset);
    }

    [Fact]
    public async Task RetryablePauseFailure_DoesNotCommitOffset()
    {
        using var directory = new TemporaryDirectory();
        var sentRequests = new List<HttpRequestMessage>();
        var handler = new TelegramHandler(PauseUpdates(10, 456, "RQ-1"), req => sentRequests.Add(req));
        using var http = new HttpClient(handler);
        var receiver = new TelegramCallbackReceiver(directory.Path, () => ("123:token", "456"), http,
            (_, _) => Task.FromResult(new TicketClaimResult(TicketClaimStatus.Claimed, "ok")),
            (_, _) => Task.FromResult(new TicketActionResult(TicketActionStatus.RetryableFailure, "lỗi mạng")));

        await receiver.CheckAsync(CancellationToken.None);

        Assert.Contains(sentRequests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("answerCallbackQuery"));
        Assert.False(File.Exists(Path.Combine(directory.Path, "telegram-123.offset")));
    }

    [Fact]
    public async Task UnauthorizedChatPause_IgnoredAndCommitsOffset()
    {
        using var directory = new TemporaryDirectory();
        var sentRequests = new List<HttpRequestMessage>();
        var handler = new TelegramHandler(PauseUpdates(10, 999, "RQ-1"), req => sentRequests.Add(req));
        using var http = new HttpClient(handler);
        var pauseCalled = false;
        var receiver = new TelegramCallbackReceiver(directory.Path, () => ("123:token", "456"), http,
            (_, _) => Task.FromResult(new TicketClaimResult(TicketClaimStatus.Claimed, "ok")),
            (_, _) =>
            {
                pauseCalled = true;
                return Task.FromResult(new TicketActionResult(TicketActionStatus.Success, "ok"));
            });

        await receiver.CheckAsync(CancellationToken.None);

        Assert.False(pauseCalled);
        Assert.Contains(sentRequests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("answerCallbackQuery"));
        var offset = await File.ReadAllTextAsync(Path.Combine(directory.Path, "telegram-123.offset"));
        Assert.Equal("11", offset);
    }

    private static string PauseUpdates(int updateId, int chatId, string code)
    {
        var updates = new[]
        {
            new
            {
                update_id = updateId,
                callback_query = new
                {
                    id = $"callback-{updateId}",
                    data = $"pause:{code}",
                    message = new { message_id = updateId, chat = new { id = chatId } }
                }
            }
        };
        return JsonSerializer.Serialize(new { ok = true, result = updates });
    }

    private static string MessageUpdates(int updateId, int chatId, string text)
    {
        var updates = new[]
        {
            new
            {
                update_id = updateId,
                message = new
                {
                    message_id = 999,
                    chat = new { id = chatId },
                    text
                }
            }
        };
        return JsonSerializer.Serialize(new { ok = true, result = updates });
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
