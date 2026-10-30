using FTMS.Domain;
using FTMS.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FTMS.Companion.Tests;

public sealed class SqliteTicketStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteTicketStore _store;

    public SqliteTicketStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ftms-test-{Guid.NewGuid():N}.db");
        _store = new SqliteTicketStore(_dbPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task CleanupAsync_DeletesClosedTicketsFromPreviousDays_AndKeepsActiveAndClosedToday()
    {
        await _store.InitializeAsync(CancellationToken.None);

        var vietnamToday = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7));
        var today = vietnamToday.Date;
        var nowToday = new DateTimeOffset(today.AddHours(10), TimeSpan.FromHours(7));
        var yesterday = new DateTimeOffset(today.AddDays(-1).AddHours(15), TimeSpan.FromHours(7));

        var activeTicket = new TicketSnapshot
        {
            Code = "RQ-ACTIVE",
            Status = TicketStatus.InProgress,
            UpdatedAt = yesterday,
            IsTerminal = false
        };

        var closedTodayTicket = new TicketSnapshot
        {
            Code = "RQ-CLOSED-TODAY",
            Status = TicketStatus.Closed,
            UpdatedAt = nowToday,
            ClosedAt = nowToday,
            IsTerminal = true
        };

        var closedYesterdayTicket = new TicketSnapshot
        {
            Code = "RQ-CLOSED-YESTERDAY",
            Status = TicketStatus.Closed,
            UpdatedAt = yesterday,
            ClosedAt = yesterday,
            IsTerminal = true
        };

        await _store.SaveSnapshotAsync(activeTicket, CancellationToken.None);
        await _store.SaveSnapshotAsync(closedTodayTicket, CancellationToken.None);
        await _store.SaveSnapshotAsync(closedYesterdayTicket, CancellationToken.None);

        // Mark terminals explicitly with their timestamps
        await _store.MarkTerminalAsync("RQ-CLOSED-TODAY", nowToday, CancellationToken.None);
        await _store.MarkTerminalAsync("RQ-CLOSED-YESTERDAY", yesterday, CancellationToken.None);

        // Add events and outbox for yesterday's closed ticket
        var eventYesterday = new TicketEvent
        {
            EventKey = "EV-1",
            TicketCode = "RQ-CLOSED-YESTERDAY",
            EventType = TicketEventType.Terminal,
            DetectedAt = yesterday,
            Reason = "closed",
            Snapshot = closedYesterdayTicket
        };
        await _store.SaveEventAndEnqueueNotificationAsync(eventYesterday, "Test message", CancellationToken.None);

        // Run daily cleanup (retentionDays = 1, meaning cutoff = start of today)
        await _store.CleanupAsync(1, CancellationToken.None);

        // Check snapshots directly from SQLite
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();

        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT code FROM ticket_snapshots ORDER BY code";
        var codes = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                codes.Add(reader.GetString(0));
            }
        }

        // RQ-ACTIVE and RQ-CLOSED-TODAY must be preserved.
        // RQ-CLOSED-YESTERDAY must have been deleted.
        Assert.Contains("RQ-ACTIVE", codes);
        Assert.Contains("RQ-CLOSED-TODAY", codes);
        Assert.DoesNotContain("RQ-CLOSED-YESTERDAY", codes);
    }

    [Fact]
    public async Task SaveSnapshotAndEventsAsync_AtomicallyPersistsAndGuardsCreatedLedger()
    {
        await _store.InitializeAsync(CancellationToken.None);

        var snapshot = new TicketSnapshot
        {
            Code = "RQ-ATOMIC-1",
            Status = TicketStatus.New,
            Title = "Yêu cầu kiểm thử",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var createdEvent = new TicketEvent
        {
            EventKey = "KEY-CREATED-1",
            TicketCode = snapshot.Code,
            EventType = TicketEventType.Created,
            CurrentStatus = TicketStatus.New,
            DetectedAt = DateTimeOffset.Now,
            Reason = "Ticket mới",
            Snapshot = snapshot
        };

        // First save: should record snapshot, event, outbox, and ledger
        await _store.SaveSnapshotAndEventsAsync(snapshot, [(createdEvent, "Tin nhắn ticket mới")], CancellationToken.None);

        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            var outboxCmd = conn.CreateCommand();
            outboxCmd.CommandText = "SELECT COUNT(1) FROM notification_outbox WHERE event_key='KEY-CREATED-1'";
            Assert.Equal(1, Convert.ToInt32(await outboxCmd.ExecuteScalarAsync()));

            var ledgerCmd = conn.CreateCommand();
            ledgerCmd.CommandText = "SELECT COUNT(1) FROM notification_ledger WHERE ticket_code='RQ-ATOMIC-1' AND notification_type='Created'";
            Assert.Equal(1, Convert.ToInt32(await ledgerCmd.ExecuteScalarAsync()));
        }

        // Second save for the same ticket's Created event (e.g. restart / re-poll): outbox must not duplicate
        var duplicateCreatedEvent = createdEvent with { EventKey = "KEY-CREATED-DUP" };
        await _store.SaveSnapshotAndEventsAsync(snapshot, [(duplicateCreatedEvent, "Tin nhắn ticket mới lặp")], CancellationToken.None);

        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            var outboxCmd = conn.CreateCommand();
            outboxCmd.CommandText = "SELECT COUNT(1) FROM notification_outbox WHERE event_key='KEY-CREATED-DUP'";
            Assert.Equal(0, Convert.ToInt32(await outboxCmd.ExecuteScalarAsync()));
        }
    }

    [Fact]
    public async Task OutboxQuery_WithLeftJoin_PicksUpItemEvenWhenEventRecordIsMissing()
    {
        await _store.InitializeAsync(CancellationToken.None);

        // Insert directly into outbox without an entry in ticket_events (orphaned outbox row)
        await using var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=True;Default Timeout=5");
        await conn.OpenAsync();
        var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO notification_outbox(event_key, message, next_attempt_at)
            VALUES('ORPHAN-KEY', 'Orphaned message', $now);
            """;
        insertCmd.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        await insertCmd.ExecuteNonQueryAsync();

        // Query using the same query as TelegramOutboxSender
        var queryCmd = conn.CreateCommand();
        queryCmd.CommandText = """
            SELECT outbox.id,outbox.event_key,outbox.message,outbox.attempt_count
            FROM notification_outbox outbox
            LEFT JOIN ticket_events event ON event.event_key=outbox.event_key
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
        queryCmd.Parameters.AddWithValue("$now", DateTimeOffset.Now.AddSeconds(1).ToString("O"));

        await using var reader = await queryCmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("ORPHAN-KEY", reader.GetString(1));
        Assert.Equal("Orphaned message", reader.GetString(2));
    }

    [Fact]
    public async Task LoadActiveSnapshotsAsync_FiltersOutCompletedAndTerminalTickets()
    {
        await _store.InitializeAsync(CancellationToken.None);

        var active0 = new TicketSnapshot
        {
            Code = "RQ-ACTIVE-0",
            Status = TicketStatus.New,
            IsTerminal = false
        };
        var active1 = new TicketSnapshot
        {
            Code = "RQ-ACTIVE-1",
            Status = TicketStatus.Assigned,
            IsTerminal = false
        };
        var active2 = new TicketSnapshot
        {
            Code = "RQ-ACTIVE-2",
            Status = TicketStatus.InProgress,
            IsTerminal = false
        };
        var active3 = new TicketSnapshot
        {
            Code = "RQ-ACTIVE-3",
            Status = TicketStatus.Paused,
            IsTerminal = false
        };
        var completed = new TicketSnapshot
        {
            Code = "RQ-COMPLETED",
            Status = TicketStatus.Completed,
            IsTerminal = false // Even if accidentally stored with IsTerminal=0
        };
        var closed = new TicketSnapshot
        {
            Code = "RQ-CLOSED",
            Status = TicketStatus.Closed,
            IsTerminal = true
        };

        await _store.SaveSnapshotsAsync([active0, active1, active2, active3, completed, closed], CancellationToken.None);

        var activeSnapshots = await _store.LoadActiveSnapshotsAsync(CancellationToken.None);

        Assert.Equal(4, activeSnapshots.Count);
        Assert.True(activeSnapshots.ContainsKey("RQ-ACTIVE-0"));
        Assert.True(activeSnapshots.ContainsKey("RQ-ACTIVE-1"));
        Assert.True(activeSnapshots.ContainsKey("RQ-ACTIVE-2"));
        Assert.True(activeSnapshots.ContainsKey("RQ-ACTIVE-3"));
        Assert.False(activeSnapshots.ContainsKey("RQ-COMPLETED"));
        Assert.False(activeSnapshots.ContainsKey("RQ-CLOSED"));
    }
}
