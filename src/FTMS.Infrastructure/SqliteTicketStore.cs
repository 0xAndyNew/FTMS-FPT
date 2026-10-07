using System.Text.Json;
using FTMS.Application;
using FTMS.Domain;
using Microsoft.Data.Sqlite;

namespace FTMS.Infrastructure;

public sealed class SqliteTicketStore(string databasePath) : ITicketStore
{
    private string ConnectionString => $"Data Source={databasePath};Pooling=True";

    public async Task InitializeAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS ticket_snapshots(code TEXT PRIMARY KEY,payload TEXT NOT NULL,is_terminal INTEGER NOT NULL,updated_at TEXT NOT NULL,terminal_at TEXT NULL);
            CREATE TABLE IF NOT EXISTS ticket_events(event_key TEXT PRIMARY KEY,ticket_code TEXT NOT NULL,event_type TEXT NOT NULL,payload TEXT NOT NULL,detected_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS notification_outbox(id INTEGER PRIMARY KEY AUTOINCREMENT,event_key TEXT NOT NULL UNIQUE,message TEXT NOT NULL,attempt_count INTEGER NOT NULL DEFAULT 0,next_attempt_at TEXT NOT NULL,sent_at TEXT NULL,last_error TEXT NULL);
            CREATE TABLE IF NOT EXISTS notification_ledger(ticket_code TEXT NOT NULL,notification_type TEXT NOT NULL,discriminator TEXT NOT NULL DEFAULT '',sent_at TEXT NOT NULL,PRIMARY KEY(ticket_code,notification_type,discriminator));
            CREATE TABLE IF NOT EXISTS telegram_claim_messages(ticket_code TEXT NOT NULL,chat_id TEXT NOT NULL,message_id INTEGER NOT NULL,sent_at TEXT NOT NULL,PRIMARY KEY(ticket_code,chat_id,message_id));
            CREATE INDEX IF NOT EXISTS idx_ticket_events_code ON ticket_events(ticket_code);
            CREATE INDEX IF NOT EXISTS idx_ticket_events_detected ON ticket_events(detected_at);
            CREATE INDEX IF NOT EXISTS idx_outbox_pending ON notification_outbox(sent_at,next_attempt_at,id);
            CREATE INDEX IF NOT EXISTS idx_claim_messages_code ON telegram_claim_messages(ticket_code);
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, TicketSnapshot>> LoadActiveSnapshotsAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, TicketSnapshot>(StringComparer.OrdinalIgnoreCase);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        var command = connection.CreateCommand(); command.CommandText = "SELECT payload FROM ticket_snapshots WHERE is_terminal=0";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var item = JsonSerializer.Deserialize<TicketSnapshot>(reader.GetString(0));
            if (item is not null) result[item.Code] = item;
        }
        return result;
    }

    public async Task SaveSnapshotsAsync(IReadOnlyList<TicketSnapshot> snapshots, CancellationToken ct)
    {
        if (snapshots.Count == 0) return;

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ticket_snapshots(code,payload,is_terminal,updated_at,terminal_at)
            VALUES($code,$payload,$terminal,$updated,$terminalAt)
            ON CONFLICT(code) DO UPDATE SET
                payload=excluded.payload,
                is_terminal=excluded.is_terminal,
                updated_at=excluded.updated_at,
                terminal_at=CASE WHEN excluded.is_terminal=1
                    THEN COALESCE(ticket_snapshots.terminal_at, excluded.terminal_at) ELSE NULL END
            """;
        var code = command.Parameters.Add("$code", SqliteType.Text);
        var payload = command.Parameters.Add("$payload", SqliteType.Text);
        var terminal = command.Parameters.Add("$terminal", SqliteType.Integer);
        var updated = command.Parameters.Add("$updated", SqliteType.Text);
        var terminalAt = command.Parameters.Add("$terminalAt", SqliteType.Text);
        var now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).ToString("O");

        foreach (var item in snapshots)
        {
            code.Value = item.Code;
            payload.Value = JsonSerializer.Serialize(item);
            terminal.Value = item.IsTerminal ? 1 : 0;
            updated.Value = now;
            terminalAt.Value = item.IsTerminal ? now : DBNull.Value;
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public Task SaveSnapshotAsync(TicketSnapshot snapshot, CancellationToken ct) =>
        SaveSnapshotsAsync([snapshot], ct);

    public Task MarkTerminalAsync(string code, DateTimeOffset terminalAt, CancellationToken ct) => ExecuteAsync(
        "UPDATE ticket_snapshots SET is_terminal=1,terminal_at=$terminalAt WHERE code=$code", ct,
        ("$terminalAt", terminalAt.ToOffset(TimeSpan.FromHours(7)).ToString("O")), ("$code", code));

    public async Task<bool> HasNotificationAsync(string ticketCode, string notificationType, string discriminator, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(ConnectionString); await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM notification_ledger WHERE ticket_code=$code AND notification_type=$type AND discriminator=$disc";
        command.Parameters.AddWithValue("$code", ticketCode);
        command.Parameters.AddWithValue("$type", notificationType);
        command.Parameters.AddWithValue("$disc", discriminator);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) > 0;
    }

    public Task RecordNotificationAsync(string ticketCode, string notificationType, string discriminator, CancellationToken ct) => ExecuteAsync(
        "INSERT OR IGNORE INTO notification_ledger(ticket_code,notification_type,discriminator,sent_at) VALUES($code,$type,$disc,$now)", ct,
        ("$code", ticketCode), ("$type", notificationType), ("$disc", discriminator), ("$now", DateTimeOffset.Now.ToString("O")));

    public Task SaveEventAsync(TicketEvent item, CancellationToken ct) => ExecuteAsync("""
        INSERT OR IGNORE INTO ticket_events(event_key,ticket_code,event_type,payload,detected_at) VALUES($key,$code,$type,$payload,$detected)
        """, ct, ("$key", item.EventKey), ("$code", item.TicketCode), ("$type", item.EventType.ToString()), ("$payload", JsonSerializer.Serialize(item)), ("$detected", item.DetectedAt.ToString("O")));

    public async Task SaveSnapshotAndEventsAsync(TicketSnapshot snapshot,
        IReadOnlyList<(TicketEvent Event, string? Message)> events, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        var snapshotCommand = connection.CreateCommand();
        snapshotCommand.Transaction = transaction;
        snapshotCommand.CommandText = """
            INSERT INTO ticket_snapshots(code,payload,is_terminal,updated_at,terminal_at)
            VALUES($code,$payload,$terminal,$updated,$terminalAt)
            ON CONFLICT(code) DO UPDATE SET
                payload=excluded.payload,
                is_terminal=excluded.is_terminal,
                updated_at=excluded.updated_at,
                terminal_at=CASE WHEN excluded.is_terminal=1
                    THEN COALESCE(ticket_snapshots.terminal_at, excluded.terminal_at) ELSE NULL END
            """;
        var now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).ToString("O");
        snapshotCommand.Parameters.AddWithValue("$code", snapshot.Code);
        snapshotCommand.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(snapshot));
        snapshotCommand.Parameters.AddWithValue("$terminal", snapshot.IsTerminal ? 1 : 0);
        snapshotCommand.Parameters.AddWithValue("$updated", now);
        snapshotCommand.Parameters.AddWithValue("$terminalAt", snapshot.IsTerminal ? now : DBNull.Value);
        await snapshotCommand.ExecuteNonQueryAsync(ct);

        foreach (var (item, message) in events)
        {
            var eventCommand = connection.CreateCommand();
            eventCommand.Transaction = transaction;
            eventCommand.CommandText = """
                INSERT OR IGNORE INTO ticket_events(event_key,ticket_code,event_type,payload,detected_at)
                VALUES($key,$code,$type,$payload,$detected)
                """;
            eventCommand.Parameters.AddWithValue("$key", item.EventKey);
            eventCommand.Parameters.AddWithValue("$code", item.TicketCode);
            eventCommand.Parameters.AddWithValue("$type", item.EventType.ToString());
            eventCommand.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(item));
            eventCommand.Parameters.AddWithValue("$detected", item.DetectedAt.ToString("O"));
            await eventCommand.ExecuteNonQueryAsync(ct);

            if (string.IsNullOrWhiteSpace(message)) continue;

            if (item.EventType == TicketEventType.Created)
            {
                var checkLedger = connection.CreateCommand();
                checkLedger.Transaction = transaction;
                checkLedger.CommandText = "SELECT COUNT(1) FROM notification_ledger WHERE ticket_code=$code AND notification_type='Created'";
                checkLedger.Parameters.AddWithValue("$code", item.TicketCode);
                if (Convert.ToInt32(await checkLedger.ExecuteScalarAsync(ct)) > 0)
                    continue;

                var recordLedger = connection.CreateCommand();
                recordLedger.Transaction = transaction;
                recordLedger.CommandText = "INSERT OR IGNORE INTO notification_ledger(ticket_code,notification_type,discriminator,sent_at) VALUES($code,'Created','',$now)";
                recordLedger.Parameters.AddWithValue("$code", item.TicketCode);
                recordLedger.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
                await recordLedger.ExecuteNonQueryAsync(ct);
            }

            if (item.EventType is TicketEventType.StatusChanged or TicketEventType.Terminal &&
                item.PreviousStatus is not null)
            {
                var transitionKey = $"{item.PreviousStatus}>{item.CurrentStatus}";
                var checkLedger = connection.CreateCommand();
                checkLedger.Transaction = transaction;
                checkLedger.CommandText = "SELECT COUNT(1) FROM notification_ledger WHERE ticket_code=$code AND notification_type=$type AND discriminator=$disc";
                checkLedger.Parameters.AddWithValue("$code", item.TicketCode);
                checkLedger.Parameters.AddWithValue("$type", item.EventType.ToString());
                checkLedger.Parameters.AddWithValue("$disc", transitionKey);
                if (Convert.ToInt32(await checkLedger.ExecuteScalarAsync(ct)) > 0)
                    continue;

                var recordLedger = connection.CreateCommand();
                recordLedger.Transaction = transaction;
                recordLedger.CommandText = "INSERT OR IGNORE INTO notification_ledger(ticket_code,notification_type,discriminator,sent_at) VALUES($code,$type,$disc,$now)";
                recordLedger.Parameters.AddWithValue("$code", item.TicketCode);
                recordLedger.Parameters.AddWithValue("$type", item.EventType.ToString());
                recordLedger.Parameters.AddWithValue("$disc", transitionKey);
                recordLedger.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
                await recordLedger.ExecuteNonQueryAsync(ct);
            }

            var outboxCommand = connection.CreateCommand();
            outboxCommand.Transaction = transaction;
            outboxCommand.CommandText = """
                INSERT OR IGNORE INTO notification_outbox(event_key,message,next_attempt_at)
                VALUES($key,$message,$next)
                """;
            outboxCommand.Parameters.AddWithValue("$key", item.EventKey);
            outboxCommand.Parameters.AddWithValue("$message", message);
            outboxCommand.Parameters.AddWithValue("$next", DateTimeOffset.Now.ToString("O"));
            await outboxCommand.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<bool> EventExistsAsync(string eventKey, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(ConnectionString); await connection.OpenAsync(ct);
        var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(1) FROM ticket_events WHERE event_key=$key"; command.Parameters.AddWithValue("$key", eventKey);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) > 0;
    }

    public Task EnqueueNotificationAsync(TicketEvent item, string message, CancellationToken ct) => ExecuteAsync(
        "INSERT OR IGNORE INTO notification_outbox(event_key,message,next_attempt_at) VALUES($key,$message,$next)", ct,
        ("$key", item.EventKey), ("$message", message), ("$next", DateTimeOffset.Now.ToString("O")));

    public async Task SaveEventAndEnqueueNotificationAsync(TicketEvent item, string? message, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO ticket_events(event_key,ticket_code,event_type,payload,detected_at)
            VALUES($key,$code,$type,$payload,$detected);
            """;
        command.Parameters.AddWithValue("$key", item.EventKey);
        command.Parameters.AddWithValue("$code", item.TicketCode);
        command.Parameters.AddWithValue("$type", item.EventType.ToString());
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(item));
        command.Parameters.AddWithValue("$detected", item.DetectedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);

        if (!string.IsNullOrWhiteSpace(message))
        {
            var outboxCmd = connection.CreateCommand();
            outboxCmd.Transaction = transaction;
            outboxCmd.CommandText = """
                INSERT OR IGNORE INTO notification_outbox(event_key,message,next_attempt_at)
                VALUES($key,$message,$next);
                """;
            outboxCmd.Parameters.AddWithValue("$key", item.EventKey);
            outboxCmd.Parameters.AddWithValue("$message", message);
            outboxCmd.Parameters.AddWithValue("$next", DateTimeOffset.Now.ToString("O"));
            await outboxCmd.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task CleanupAsync(int days, CancellationToken ct)
    {
        var todayVietnam = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).Date;
        var startOfToday = new DateTimeOffset(todayVietnam, TimeSpan.FromHours(7));
        var cutoff = (days <= 1 ? startOfToday : DateTimeOffset.Now.AddDays(-Math.Max(1, days))).ToString("O");
        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync(ct);
            var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM notification_outbox
                WHERE sent_at IS NOT NULL AND sent_at < $cutoff;

                DELETE FROM notification_outbox
                WHERE sent_at IS NULL AND (TRIM(message)='' OR message LIKE $missingBody);

                DELETE FROM ticket_events
                WHERE detected_at < $cutoff AND event_key NOT IN (
                    SELECT event_key FROM notification_outbox WHERE sent_at IS NULL
                );

                DELETE FROM notification_outbox
                WHERE event_key IN (
                    SELECT event_key FROM ticket_events
                    WHERE ticket_code IN (
                        SELECT code FROM ticket_snapshots
                        WHERE is_terminal=1 AND COALESCE(terminal_at, updated_at) < $cutoff
                    )
                );

                DELETE FROM ticket_events
                WHERE ticket_code IN (
                    SELECT code FROM ticket_snapshots
                    WHERE is_terminal=1 AND COALESCE(terminal_at, updated_at) < $cutoff
                );

                DELETE FROM ticket_snapshots
                WHERE is_terminal=1 AND COALESCE(terminal_at, updated_at) < $cutoff;

                DELETE FROM notification_ledger
                WHERE ticket_code IN (
                    SELECT nl.ticket_code FROM notification_ledger nl
                    LEFT JOIN ticket_snapshots ts ON ts.code=nl.ticket_code
                    WHERE ts.code IS NULL AND nl.sent_at < $cutoff
                );

                DELETE FROM telegram_claim_messages
                WHERE sent_at < $cutoff OR ticket_code IN (
                    SELECT code FROM ticket_snapshots
                    WHERE is_terminal=1 AND COALESCE(terminal_at, updated_at) < $cutoff
                );

                PRAGMA optimize;
                """;
            command.Parameters.AddWithValue("$cutoff", cutoff);
            command.Parameters.AddWithValue("$missingBody", "%Kh\u00f4ng l\u1ea5y \u0111\u01b0\u1ee3c n\u1ed9i dung email.%");
            await command.ExecuteNonQueryAsync(ct);
        }

        await CompactIfNeededAsync(ct);
    }

    private async Task CompactIfNeededAsync(CancellationToken ct)
    {
        SqliteConnection.ClearAllPools();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);

        var pageCountCommand = connection.CreateCommand();
        pageCountCommand.CommandText = "PRAGMA page_count";
        var pageCount = Convert.ToInt64(await pageCountCommand.ExecuteScalarAsync(ct));
        var freeCountCommand = connection.CreateCommand();
        freeCountCommand.CommandText = "PRAGMA freelist_count";
        var freeCount = Convert.ToInt64(await freeCountCommand.ExecuteScalarAsync(ct));

        var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        await checkpoint.ExecuteNonQueryAsync(ct);
        if (pageCount == 0 || freeCount * 5 < pageCount) return;

        var vacuum = connection.CreateCommand();
        vacuum.CommandText = "VACUUM";
        await vacuum.ExecuteNonQueryAsync(ct);
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection(ConnectionString); await connection.OpenAsync(ct);
        var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync(ct);
    }
}
