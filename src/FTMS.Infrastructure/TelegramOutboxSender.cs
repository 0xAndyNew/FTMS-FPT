using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FTMS.Application;
using FTMS.Domain;
using Microsoft.Data.Sqlite;

namespace FTMS.Infrastructure;

public sealed class TelegramOutboxSender(string databasePath, Func<(string Token, string ChatId)> settings, HttpClient http) : INotificationSender
{
    public async Task SendPendingAsync(CancellationToken ct)
    {
        var telegram = settings();
        if (string.IsNullOrWhiteSpace(telegram.Token) || string.IsNullOrWhiteSpace(telegram.ChatId)) return;
        await using var connection = new SqliteConnection($"Data Source={databasePath}"); await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id,event_key,message,attempt_count FROM notification_outbox WHERE sent_at IS NULL AND next_attempt_at<=$now ORDER BY id LIMIT 20";
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        var items = new List<(long Id, string EventKey, string Message, int Attempts)>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) items.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        foreach (var item in items)
        {
            try
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
                    catch { }
                }

                var canReceive = CanReceiveTicket(eventItem, decodedMessage);
                var route = code.StartsWith("CA", StringComparison.OrdinalIgnoreCase) || code.StartsWith("AL", StringComparison.OrdinalIgnoreCase)
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
                    var response = await http.PostAsJsonAsync($"https://api.telegram.org/bot{telegram.Token}/sendMessage", payload, ct);
                    response.EnsureSuccessStatusCode();

                    if (isFirst && canReceive && !string.IsNullOrWhiteSpace(code))
                    {
                        try
                        {
                            var resJson = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                            if (resJson.TryGetProperty("result", out var resElem) &&
                                resElem.TryGetProperty("message_id", out var msgIdElem) &&
                                msgIdElem.TryGetInt64(out var msgId))
                            {
                                await SaveClaimMessageAsync(connection, code, telegram.ChatId, msgId, ct);
                            }
                        }
                        catch { }
                    }
                }

                // Nếu ticket đã có người nhận hoặc không còn ở trạng thái chờ nhận,
                // tự động ẩn/gỡ bỏ nút "🙋 Nhận ticket" trên tất cả các tin nhắn Telegram trước đó của ticket này
                if (!canReceive && !string.IsNullOrWhiteSpace(code))
                {
                    await RemoveAllClaimButtonsForTicketAsync(connection, telegram.Token, telegram.ChatId, code, openUrl, ct);
                }

                await MarkSentAndCleanupAsync(connection, item.Id, item.EventKey, ct);
            }
            catch (Exception ex)
            {
                await UpdateFailureAsync(connection, item.Id,
                    TelegramErrorSanitizer.Sanitize(ex.Message, telegram.Token), item.Attempts + 1, ct);
            }
        }
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
            while (await reader.ReadAsync(ct))
                messageIds.Add(reader.GetInt64(0));
        }

        if (messageIds.Count == 0) return;

        foreach (var messageId in messageIds)
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
                await http.PostAsJsonAsync($"https://api.telegram.org/bot{token}/editMessageReplyMarkup", payload, ct);
            }
            catch { }
        }

        var deleteCmd = connection.CreateCommand();
        deleteCmd.CommandText = "DELETE FROM telegram_claim_messages WHERE ticket_code=$code AND chat_id=$chatId";
        deleteCmd.Parameters.AddWithValue("$code", code);
        deleteCmd.Parameters.AddWithValue("$chatId", chatId);
        await deleteCmd.ExecuteNonQueryAsync(ct);
    }

    private static IReadOnlyList<string> SplitMessageIfExceedsLimit(string message, int limit = 4000)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length <= limit)
            return [message];

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

        if (!string.IsNullOrWhiteSpace(remaining))
            parts.Add(remaining);

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
                // Xóa outbox, events, snapshots nhưng GIỮ LẠI notification_ledger
                // để đảm bảo "TICKET MỚI" và reminder milestone không bị gửi lại
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

    private static async Task UpdateFailureAsync(SqliteConnection connection, long id, string? error, int attempts, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE notification_outbox SET attempt_count=$attempts,next_attempt_at=$next,last_error=$error WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$attempts", attempts);
        command.Parameters.AddWithValue("$next", DateTimeOffset.Now.AddSeconds(Math.Min(300, Math.Pow(2, attempts))).ToString("O"));
        command.Parameters.AddWithValue("$error", error ?? string.Empty);
        await command.ExecuteNonQueryAsync(ct);
    }
}
