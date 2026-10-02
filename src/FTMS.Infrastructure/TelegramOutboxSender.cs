using System.Net.Http.Json;
using System.Net;
using System.Text.RegularExpressions;
using FTMS.Application;
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
                var canReceive = decodedMessage.Contains("Ticket mới", StringComparison.OrdinalIgnoreCase) ||
                    decodedMessage.Contains("Nhắc ticket chưa được nhận", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(decodedMessage,
                    @"Trạng thái:</b>\s*(?:[^\r\n]*➔\s*)?(?:Tạo mới|Phân công)\s*(?:\r?\n|$)",
                    RegexOptions.IgnoreCase);
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
