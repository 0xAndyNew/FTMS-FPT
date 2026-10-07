using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FTMS.Application;
using FTMS.Domain;
using Microsoft.Data.Sqlite;

namespace FTMS.Infrastructure;

public sealed class TelegramOutboxSender(
    string databasePath,
    Func<(string Token, string ChatId)> settings,
    HttpClient http) : INotificationSender
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly object _wakeLock = new();
    private TaskCompletionSource<bool> _wakeTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _running;

    public void Signal()
    {
        lock (_wakeLock)
        {
            _wakeTcs.TrySetResult(true);
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var nextAttemptAt = await SendNextPendingAsync(ct);
                if (nextAttemptAt == DateTimeOffset.MinValue) continue;

                var delay = nextAttemptAt is null
                    ? TimeSpan.FromSeconds(30)
                    : nextAttemptAt.Value - DateTimeOffset.Now;
                delay = TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 50, 30_000));

                Task wakeTask;
                lock (_wakeLock)
                {
                    wakeTask = _wakeTcs.Task;
                    if (wakeTask.IsCompleted)
                        _wakeTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                await Task.WhenAny(wakeTask, Task.Delay(delay, ct));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    // DateTimeOffset.MinValue means an item was processed and the caller should query again immediately.
    private async Task<DateTimeOffset?> SendNextPendingAsync(CancellationToken ct)
    {
        var telegram = settings();
        if (string.IsNullOrWhiteSpace(telegram.Token) || string.IsNullOrWhiteSpace(telegram.ChatId))
            return null;

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT outbox.id,outbox.event_key,outbox.message,outbox.attempt_count
            FROM notification_outbox outbox
            JOIN ticket_events event ON event.event_key=outbox.event_key
            WHERE outbox.sent_at IS NULL AND outbox.next_attempt_at<=$now
            ORDER BY CASE event.event_type
                WHEN 'Created' THEN 0
                WHEN 'StatusChanged' THEN 0
                WHEN 'AssignmentChanged' THEN 0
                WHEN 'Terminal' THEN 0
                WHEN 'EmailReceived' THEN 1
                WHEN 'SlaThresholdReached' THEN 1
                ELSE 2 END,
                outbox.id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));

        (long Id, string EventKey, string Message, int Attempts)? item = null;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
                item = (reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3));
        }

        if (item is null)
        {
            var nextCommand = connection.CreateCommand();
            nextCommand.CommandText = "SELECT MIN(next_attempt_at) FROM notification_outbox WHERE sent_at IS NULL";
            var nextValue = await nextCommand.ExecuteScalarAsync(ct);
            return nextValue is string nextText && DateTimeOffset.TryParse(nextText, out var next)
                ? next
                : null;
        }

        try
        {
            await SendItemAsync(connection, item.Value, telegram, ct);
            await MarkSentAndCleanupAsync(connection, item.Value.Id, item.Value.EventKey, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var retryAfter = ex is TelegramRequestException telegramError
                ? telegramError.RetryAfterSeconds
                : null;
            await UpdateFailureAsync(connection, item.Value.Id,
                TelegramErrorSanitizer.Sanitize(ex.Message, telegram.Token),
                item.Value.Attempts + 1, retryAfter, ct);
        }

        return DateTimeOffset.MinValue;
    }

    private async Task SendItemAsync(SqliteConnection connection,
        (long Id, string EventKey, string Message, int Attempts) item,
        (string Token, string ChatId) telegram,
        CancellationToken ct)
    {
        var decodedMessage = WebUtility.HtmlDecode(item.Message);
        var code = Regex.Match(decodedMessage, @"Mã (?:RQ|ticket|request):(?:</b>)?\s*(?:<code>)?([^\s<]+)",
            RegexOptions.IgnoreCase).Groups[1].Value;

        TicketEvent? eventItem = null;
        var eventCommand = connection.CreateCommand();
        eventCommand.CommandText = "SELECT payload FROM ticket_events WHERE event_key=$eventKey";
        eventCommand.Parameters.AddWithValue("$eventKey", item.EventKey);
        var eventPayload = (string?)await eventCommand.ExecuteScalarAsync(ct);
        if (!string.IsNullOrWhiteSpace(eventPayload))
        {
            try { eventItem = JsonSerializer.Deserialize<TicketEvent>(eventPayload); }
            catch (JsonException) { }
        }

        var canReceive = CanReceiveTicket(eventItem, decodedMessage);
        var route = code.StartsWith("CA", StringComparison.OrdinalIgnoreCase) ||
                    code.StartsWith("AL", StringComparison.OrdinalIgnoreCase)
            ? "case" : "request";
        var openUrl = $"https://ftms.fpt.net/ihub/{route}/edit/{Uri.EscapeDataString(code)}";
        object[][] buttons = canReceive
            ? [[new { text = "🔎 Mở ticket", url = openUrl }, new { text = "🙋 Nhận ticket", callback_data = $"receive:{code}" }]]
            : [[new { text = "🔎 Mở ticket", url = openUrl }]];

        var parts = SplitMessageIfExceedsLimit(item.Message, 4000);
        for (var i = 0; i < parts.Count; i++)
        {
            var isFirst = i == 0;
            object payload = isFirst
                ? new { chat_id = telegram.ChatId, text = parts[i], parse_mode = "HTML", disable_web_page_preview = true, reply_markup = new { inline_keyboard = buttons } }
                : new { chat_id = telegram.ChatId, text = parts[i], parse_mode = "HTML", disable_web_page_preview = true };
            using var response = await PostTelegramAsync(
                $"https://api.telegram.org/bot{telegram.Token}/sendMessage", payload, ct);
            var responseJson = await EnsureTelegramSuccessAsync(response, ct);

            if (isFirst && canReceive && !string.IsNullOrWhiteSpace(code))
            {
                try
                {
                    using var document = JsonDocument.Parse(responseJson);
                    if (document.RootElement.TryGetProperty("result", out var result) &&
                        result.TryGetProperty("message_id", out var messageId) &&
                        messageId.TryGetInt64(out var parsedMessageId))
                    {
                        await SaveClaimMessageAsync(connection, code, telegram.ChatId, parsedMessageId, ct);
                    }
                }
                catch (JsonException) { }
            }
        }

        if (!canReceive && !string.IsNullOrWhiteSpace(code))
            await RemoveAllClaimButtonsForTicketAsync(
                connection, telegram.Token, telegram.ChatId, code, openUrl, ct);
    }

    private async Task<HttpResponseMessage> PostTelegramAsync(string url, object payload, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            return await http.PostAsJsonAsync(url, payload, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Telegram không phản hồi trong {RequestTimeout.TotalSeconds:0} giây.");
        }
    }

    private static async Task<string> EnsureTelegramSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode) return body;

        int? retryAfter = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("parameters", out var parameters) &&
                parameters.TryGetProperty("retry_after", out var value) && value.TryGetInt32(out var seconds))
                retryAfter = seconds;
        }
        catch (JsonException) { }

        throw new TelegramRequestException(
            $"Telegram HTTP {(int)response.StatusCode}: {body}", retryAfter);
    }

    public static bool CanReceiveTicket(TicketEvent? evt, string decodedMessage)
    {
        if (evt is not null)
        {
            var s = evt.Snapshot;
            var hasAssignee = (s.AssigneeId is not null and not 0) ||
                (!string.IsNullOrWhiteSpace(s.AssigneeName) && s.AssigneeName.Trim() != "---" && s.AssigneeName.Trim() != "Chưa nhận");
            if (hasAssignee) return false;
            if (s.Status is not (TicketStatus.New or TicketStatus.Assigned)) return false;
            if (evt.EventType is TicketEventType.Terminal or TicketEventType.AssignmentChanged) return false;
            return evt.EventType is TicketEventType.Created or TicketEventType.UnassignedReminder or TicketEventType.StatusChanged or TicketEventType.SlaThresholdReached or TicketEventType.EmailReceived;
        }

        if (decodedMessage.Contains("TICKET ĐÃ CÓ NGƯỜI NHẬN", StringComparison.OrdinalIgnoreCase) ||
            decodedMessage.Contains("TICKET ĐÃ CHUYỂN NGƯỜI XỬ LÝ", StringComparison.OrdinalIgnoreCase) ||
            decodedMessage.Contains("TICKET ĐÃ ĐÓNG", StringComparison.OrdinalIgnoreCase) ||
            decodedMessage.Contains("TICKET ĐÃ HỦY", StringComparison.OrdinalIgnoreCase) ||
            decodedMessage.Contains("TICKET KHÔNG XỬ LÝ", StringComparison.OrdinalIgnoreCase) ||
            decodedMessage.Contains("TICKET KẾT THÚC", StringComparison.OrdinalIgnoreCase))
            return false;

        var assigneeMatch = Regex.Match(decodedMessage, @"(?:Người xử lý|Người nhận):</b>\s*(?:[^\r\n]*➔\s*)?([^\r\n<]+)", RegexOptions.IgnoreCase);
        if (assigneeMatch.Success)
        {
            var name = assigneeMatch.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(name) && name != "Chưa nhận" && name != "---" && name != "Chưa gán")
                return false;
        }

        if (Regex.IsMatch(decodedMessage, @"Trạng thái:</b>\s*(?:[^\r\n]*➔\s*)?(?:Đang thực hiện|Tạm ngưng|Hoàn thành|Đã đóng|Đã hủy|Không xử lý)\s*(?:\r?\n|$)", RegexOptions.IgnoreCase))
            return false;

        return decodedMessage.Contains("Ticket mới", StringComparison.OrdinalIgnoreCase) ||
            decodedMessage.Contains("Nhắc ticket chưa được nhận", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(decodedMessage, @"Trạng thái:</b>\s*(?:[^\r\n]*➔\s*)?(?:Tạo mới|Phân công)\s*(?:\r?\n|$)", RegexOptions.IgnoreCase);
    }

    private static async Task SaveClaimMessageAsync(SqliteConnection connection, string code, string chatId, long messageId, CancellationToken ct)
    {
        var insertCmd = connection.CreateCommand();
        insertCmd.CommandText = """
            INSERT OR IGNORE INTO telegram_claim_messages(ticket_code, chat_id, message_id, sent_at)
            VALUES($code, $chatId, $msgId, $sentAt)
            """;
        insertCmd.Parameters.AddWithValue("$code", code);
        insertCmd.Parameters.AddWithValue("$chatId", chatId);
        insertCmd.Parameters.AddWithValue("$msgId", messageId);
        insertCmd.Parameters.AddWithValue("$sentAt", DateTimeOffset.Now.ToString("O"));
        await insertCmd.ExecuteNonQueryAsync(ct);
    }

    private async Task RemoveAllClaimButtonsForTicketAsync(SqliteConnection connection, string token, string chatId, string code, string openUrl, CancellationToken ct)
    {
        var messageIds = new List<long>();
        var selectCmd = connection.CreateCommand();
        selectCmd.CommandText = "SELECT message_id FROM telegram_claim_messages WHERE ticket_code=$code AND chat_id=$chatId";
        selectCmd.Parameters.AddWithValue("$code", code);
        selectCmd.Parameters.AddWithValue("$chatId", chatId);
        await using (var reader = await selectCmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) messageIds.Add(reader.GetInt64(0));
        }

        var updateTasks = messageIds.Select(async messageId =>
        {
            try
            {
                var payload = new
                {
                    chat_id = chatId,
                    message_id = messageId,
                    reply_markup = new
                    {
                        inline_keyboard = new object[][]
                        {
                            [new { text = "🔎 Mở ticket", url = openUrl }]
                        }
                    }
                };
                using var response = await PostTelegramAsync(
                    $"https://api.telegram.org/bot{token}/editMessageReplyMarkup", payload, ct);
                await EnsureTelegramSuccessAsync(response, ct);
                return messageId;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return 0L; }
        });

        var results = await Task.WhenAll(updateTasks);
        foreach (var messageId in results)
        {
            if (messageId <= 0) continue;
            try
            {
                var deleteCommand = connection.CreateCommand();
                deleteCommand.CommandText = """
                    DELETE FROM telegram_claim_messages
                    WHERE ticket_code=$code AND chat_id=$chatId AND message_id=$messageId
                    """;
                deleteCommand.Parameters.AddWithValue("$code", code);
                deleteCommand.Parameters.AddWithValue("$chatId", chatId);
                deleteCommand.Parameters.AddWithValue("$messageId", messageId);
                await deleteCommand.ExecuteNonQueryAsync(ct);
            }
            catch { }
        }
    }

    private static IReadOnlyList<string> SplitMessageIfExceedsLimit(string message, int limit = 4000)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length <= limit) return [message];

        var parts = new List<string>();
        var remaining = message;
        var partIndex = 1;
        while (remaining.Length > limit)
        {
            var splitIndex = remaining.LastIndexOf('\n', limit);
            if (splitIndex < limit / 2) splitIndex = limit;
            var part = remaining[..splitIndex];
            remaining = remaining[splitIndex..].TrimStart('\r', '\n');
            var inBlockquote = part.Contains("<blockquote>", StringComparison.OrdinalIgnoreCase) &&
                               !part.Contains("</blockquote>", StringComparison.OrdinalIgnoreCase);
            if (inBlockquote)
            {
                part += "\n</blockquote>";
                if (!remaining.StartsWith("<blockquote>", StringComparison.OrdinalIgnoreCase))
                    remaining = $"<b>Phần {partIndex + 1}:</b>\n<blockquote>\n{remaining}";
            }
            parts.Add(part);
            partIndex++;
        }
        if (!string.IsNullOrWhiteSpace(remaining)) parts.Add(remaining);
        return parts;
    }

    private static async Task MarkSentAndCleanupAsync(SqliteConnection connection, long id, string eventKey, CancellationToken ct)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE notification_outbox SET sent_at=$now,last_error=NULL WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);

        var ticketCommand = connection.CreateCommand();
        ticketCommand.Transaction = transaction;
        ticketCommand.CommandText = "SELECT ticket_code FROM ticket_events WHERE event_key=$eventKey";
        ticketCommand.Parameters.AddWithValue("$eventKey", eventKey);
        var ticketCode = (string?)await ticketCommand.ExecuteScalarAsync(ct);
        if (!string.IsNullOrWhiteSpace(ticketCode))
        {
            var canCleanup = connection.CreateCommand();
            canCleanup.Transaction = transaction;
            canCleanup.CommandText = """
                SELECT CASE WHEN
                    EXISTS(SELECT 1 FROM ticket_events WHERE ticket_code=$code AND event_type='Terminal') AND
                    NOT EXISTS(
                        SELECT 1 FROM notification_outbox pending
                        JOIN ticket_events pending_event ON pending_event.event_key=pending.event_key
                        WHERE pending_event.ticket_code=$code AND pending.sent_at IS NULL
                    )
                THEN 1 ELSE 0 END
                """;
            canCleanup.Parameters.AddWithValue("$code", ticketCode);
            if (Convert.ToInt32(await canCleanup.ExecuteScalarAsync(ct)) == 1)
            {
                var cleanup = connection.CreateCommand();
                cleanup.Transaction = transaction;
                cleanup.CommandText = """
                    DELETE FROM notification_outbox WHERE event_key IN (
                        SELECT event_key FROM ticket_events WHERE ticket_code=$code
                    );
                    DELETE FROM ticket_events WHERE ticket_code=$code;
                    DELETE FROM ticket_snapshots WHERE code=$code;
                    """;
                cleanup.Parameters.AddWithValue("$code", ticketCode);
                await cleanup.ExecuteNonQueryAsync(ct);
            }
        }
        await transaction.CommitAsync(ct);
    }

    private static async Task UpdateFailureAsync(SqliteConnection connection, long id, string? error,
        int attempts, int? retryAfterSeconds, CancellationToken ct)
    {
        var delaySeconds = retryAfterSeconds is > 0
            ? retryAfterSeconds.Value
            : (int)Math.Min(300, Math.Pow(2, attempts));
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE notification_outbox
            SET attempt_count=$attempts,next_attempt_at=$next,last_error=$error
            WHERE id=$id
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$attempts", attempts);
        command.Parameters.AddWithValue("$next", DateTimeOffset.Now.AddSeconds(delaySeconds).ToString("O"));
        command.Parameters.AddWithValue("$error", error ?? string.Empty);
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed class TelegramRequestException(string message, int? retryAfterSeconds) : Exception(message)
    {
        public int? RetryAfterSeconds { get; } = retryAfterSeconds;
    }
}
