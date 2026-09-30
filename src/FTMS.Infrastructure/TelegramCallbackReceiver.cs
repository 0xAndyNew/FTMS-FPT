using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FTMS.Domain;

namespace FTMS.Infrastructure;

public sealed class TelegramCallbackReceiver(
    string stateDirectory,
    Func<(string Token, string ChatId)> settings,
    HttpClient http,
    Func<string, CancellationToken, Task<TicketClaimResult>> claimTicket,
    Action<string>? reportStatus = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task CheckAsync(CancellationToken cancellationToken, int timeoutSeconds = 0)
    {
        if (!await _gate.WaitAsync(0, cancellationToken)) return;
        try
        {
            await CheckInternalAsync(cancellationToken, timeoutSeconds);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CheckInternalAsync(CancellationToken cancellationToken, int timeoutSeconds)
    {
        var telegram = settings();
        if (string.IsNullOrWhiteSpace(telegram.Token) || string.IsNullOrWhiteSpace(telegram.ChatId)) return;

        var offsetPath = GetOffsetPath(telegram.Token);
        var offset = await ReadOffsetAsync(offsetPath, cancellationToken);
        var timeout = Math.Max(0, timeoutSeconds);
        using var response = await http.GetAsync(
            $"https://api.telegram.org/bot{telegram.Token}/getUpdates?offset={offset}&timeout={timeout}&allowed_updates=%5B%22callback_query%22%5D",
            cancellationToken);
        var root = await ReadTelegramResponseAsync(response, cancellationToken);
        if (!root.TryGetProperty("result", out var results) || results.ValueKind != JsonValueKind.Array) return;

        foreach (var update in results.EnumerateArray()
            .Where(x => x.TryGetProperty("update_id", out var id) && id.TryGetInt64(out _))
            .OrderBy(x => x.GetProperty("update_id").GetInt64()))
        {
            var updateId = update.GetProperty("update_id").GetInt64();
            if (!update.TryGetProperty("callback_query", out var callback))
            {
                await WriteOffsetAsync(offsetPath, updateId + 1, cancellationToken);
                continue;
            }

            var callbackId = callback.TryGetProperty("id", out var callbackIdElement)
                ? callbackIdElement.GetString()
                : null;
            var callbackChatId = callback.TryGetProperty("message", out var callbackMessage) &&
                callbackMessage.TryGetProperty("chat", out var callbackChat) &&
                callbackChat.TryGetProperty("id", out var callbackChatIdElement)
                ? callbackChatIdElement.ToString()
                : string.Empty;
            if (!string.Equals(callbackChatId, telegram.ChatId.Trim(), StringComparison.Ordinal))
            {
                await AnswerBestEffortAsync(telegram.Token, callbackId,
                    "Chat này không được phép thao tác ticket.", true, cancellationToken);
                await WriteOffsetAsync(offsetPath, updateId + 1, cancellationToken);
                continue;
            }

            var data = callback.TryGetProperty("data", out var dataElement) ? dataElement.GetString() : null;
            if (data?.StartsWith("receive:", StringComparison.Ordinal) != true)
            {
                await WriteOffsetAsync(offsetPath, updateId + 1, cancellationToken);
                continue;
            }

            var code = data["receive:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(code))
            {
                await AnswerBestEffortAsync(telegram.Token, callbackId,
                    "Mã ticket không hợp lệ.", true, cancellationToken);
                await WriteOffsetAsync(offsetPath, updateId + 1, cancellationToken);
                continue;
            }
            var result = await claimTicket(code, cancellationToken);
            if (result.IsRetryable)
            {
                await AnswerBestEffortAsync(telegram.Token, callbackId,
                    $"Chưa thể nhận {code}: {result.Message}", true, cancellationToken);
                reportStatus?.Invoke(TelegramErrorSanitizer.Sanitize(
                    $"Telegram sẽ thử lại {code}: {result.Message}", telegram.Token));
                break;
            }

            var editMarkupTask = Task.CompletedTask;
            if (callback.TryGetProperty("message", out var sourceMessage) &&
                sourceMessage.TryGetProperty("message_id", out var messageIdElement))
            {
                var route = code.StartsWith("CA", StringComparison.OrdinalIgnoreCase) ||
                    code.StartsWith("AL", StringComparison.OrdinalIgnoreCase) ? "case" : "request";
                editMarkupTask = PostBestEffortAsync(telegram.Token, "editMessageReplyMarkup", new
                {
                    chat_id = callbackChatId,
                    message_id = messageIdElement.GetInt64(),
                    reply_markup = new
                    {
                        inline_keyboard = new object[][]
                        {
                            [new { text = "🔎 Mở ticket", url = $"https://ftms.fpt.net/ihub/{route}/edit/{Uri.EscapeDataString(code)}" }]
                        }
                    }
                }, cancellationToken);
            }

            var successText = result.Status switch
            {
                TicketClaimStatus.Claimed => $"Đã nhận {code} trên FTMS",
                TicketClaimStatus.AlreadyOwnedByCurrentUser => $"{code} đã thuộc tài khoản FTMS hiện tại",
                _ => result.Message
            };
            var answerTask = AnswerBestEffortAsync(telegram.Token, callbackId, successText, !result.IsSuccess, cancellationToken);
            await Task.WhenAll(editMarkupTask, answerTask);
            await WriteOffsetAsync(offsetPath, updateId + 1, cancellationToken);
        }
    }

    private string GetOffsetPath(string token)
    {
        var separator = token.IndexOf(':');
        var botId = separator > 0 && token[..separator].All(char.IsDigit)
            ? token[..separator]
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..16];
        return Path.Combine(stateDirectory, $"telegram-{botId}.offset");
    }

    private static async Task<long> ReadOffsetAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return 0;
        var value = await File.ReadAllTextAsync(path, cancellationToken);
        return long.TryParse(value, out var offset) ? offset : 0;
    }

    private static async Task WriteOffsetAsync(string path, long offset, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, offset.ToString(), cancellationToken);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private async Task<JsonElement> ReadTelegramResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Telegram HTTP {(int)response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
        {
            var description = root.TryGetProperty("description", out var value) ? value.GetString() : body;
            throw new InvalidOperationException($"Telegram: {description}");
        }
        return root.Clone();
    }

    private async Task AnswerBestEffortAsync(string token, string? callbackId, string text, bool showAlert,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(callbackId)) return;
        await PostBestEffortAsync(token, "answerCallbackQuery", new
        {
            callback_query_id = callbackId,
            text,
            show_alert = showAlert
        }, cancellationToken);
    }

    private async Task PostBestEffortAsync(string token, string method, object payload, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.PostAsJsonAsync($"https://api.telegram.org/bot{token}/{method}", payload, cancellationToken);
            _ = await ReadTelegramResponseAsync(response, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            reportStatus?.Invoke($"Lỗi cập nhật Telegram: {TelegramErrorSanitizer.Sanitize(ex.Message, token)}");
        }
    }
}
