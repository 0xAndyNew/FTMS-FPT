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
}
