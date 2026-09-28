using FTMS.Application;
using FTMS.Domain;
using Xunit;

namespace FTMS.Companion.Tests;

public sealed class TicketMonitorTests
{
    [Fact]
    public async Task CountsTicketsClosedTodayByLoggedInCloser()
    {
        var user = new CurrentUserIdentity(42, "closer.user", null, null);
        var now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7));
        var client = new FakeFtmsClient(user,
        [
            Closed("RQ-1", now, 42, "closer.user", assigneeId: 10),
            Closed("RQ-2", now, 99, "other.user", assigneeId: 42),
            Closed("rq-1", now, 42, "closer.user", assigneeId: 10),
            Closed("RQ-3", now.AddDays(-1), 42, "closer.user", assigneeId: 42)
        ]);
        var monitor = CreateMonitor(client);
        DashboardSummary? summary = null;
        monitor.SummaryChanged += value => summary = value;

        await monitor.InitializeAsync(CancellationToken.None);
        await monitor.SyncNowAsync(CancellationToken.None);

        Assert.NotNull(summary);
        Assert.Equal(1, summary.PersonalClosedToday);
    }

    [Fact]
    public async Task FallsBackToCloserNameOnlyWhenCloserIdIsMissing()
    {
        var now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7));
        var client = new FakeFtmsClient(new CurrentUserIdentity(42, " Closer.User ", null, null),
        [
            Closed("RQ-1", now, null, "closer.user", assigneeId: 99),
            Closed("RQ-2", now, 99, "closer.user", assigneeId: 42)
        ]);
        var monitor = CreateMonitor(client);
        DashboardSummary? summary = null;
        monitor.SummaryChanged += value => summary = value;

        await monitor.InitializeAsync(CancellationToken.None);
        await monitor.SyncNowAsync(CancellationToken.None);

        Assert.Equal(1, summary?.PersonalClosedToday);
    }

    [Fact]
    public async Task RunsDailyCleanupOnlyOncePerDayAcrossMultiplePolls()
    {
        var store = new MemoryStore();
        var client = new FakeFtmsClient(null, []);
        var monitor = new TicketMonitor(client, store, new NullSender(), new TicketChangeDetector(), new AppSettings());

        var cleanupCompletedFired = 0;
        monitor.DailyCleanupCompleted += () => cleanupCompletedFired++;

        await monitor.InitializeAsync(CancellationToken.None);
        Assert.Equal(1, store.CleanupCallCount);
        Assert.Equal(1, cleanupCompletedFired);

        // Multiple subsequent syncs on the same day should NOT trigger CleanupAsync again
        await monitor.SyncNowAsync(CancellationToken.None);
        await monitor.SyncNowAsync(CancellationToken.None);
        await monitor.SyncNowAsync(CancellationToken.None);

        Assert.Equal(1, store.CleanupCallCount);
        Assert.Equal(1, cleanupCompletedFired);
        Assert.True(monitor.LastCleanupDay > 0);
    }

    [Fact]
    public async Task Poll_ActiveOnly_WhenNoMissingTicketsAndIntervalNotElapsed()
    {
        var user = new CurrentUserIdentity(42, "closer.user", null, null);
        var activeTicket = new TicketSnapshot { Code = "RQ-ACTIVE", Status = TicketStatus.InProgress };
        var client = new FakeFtmsClient(user, [activeTicket], []);
        var monitor = CreateMonitor(client);

        await monitor.InitializeAsync(CancellationToken.None);
        await monitor.SyncNowAsync(CancellationToken.None);

        Assert.Equal(1, client.GetTicketsWithHistoryCount);
        Assert.Equal(0, client.GetTicketsActiveOnlyCount);

        // Second sync immediately after
        await monitor.SyncNowAsync(CancellationToken.None);

        Assert.Equal(1, client.GetTicketsWithHistoryCount);
        Assert.Equal(1, client.GetTicketsActiveOnlyCount);
        Assert.Equal(0, client.GetClosedTicketsCallCount);
    }

    [Fact]
    public async Task Poll_WhenMonitoredTicketDisappears_ImmediatelyFetchesHistoryAndEmitsTerminal()
    {
        var user = new CurrentUserIdentity(42, "closer.user", null, null);
        var now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7));
        var activeTicket = new TicketSnapshot { Code = "RQ-100", Status = TicketStatus.InProgress, DepartmentName = "TOC - Phòng Dịch vụ Data Center" };
        var client = new FakeFtmsClient(user, [activeTicket], []);
        var store = new MemoryStore();
        var monitor = new TicketMonitor(client, store, new NullSender(), new TicketChangeDetector(), new AppSettings());

        await monitor.InitializeAsync(CancellationToken.None);
        await monitor.SyncNowAsync(CancellationToken.None);

        // Now RQ-100 disappears from active list and appears in closed history
        client.CurrentTickets = [];
        client.CurrentClosedTickets = [Closed("RQ-100", now, 42, "closer.user", assigneeId: 42)];

        await monitor.SyncNowAsync(CancellationToken.None);

        // History was immediately probed
        Assert.Equal(1, client.GetClosedTicketsCallCount);
        Assert.Contains(store.SavedEvents, e => e.TicketCode == "RQ-100" && e.EventType == TicketEventType.Terminal);
    }

    [Fact]
    public async Task SyncNow_WithForceHistory_ImmediatelyFetchesHistory()
    {
        var user = new CurrentUserIdentity(42, "closer.user", null, null);
        var client = new FakeFtmsClient(user, [], []);
        var monitor = CreateMonitor(client);

        await monitor.InitializeAsync(CancellationToken.None);
        await monitor.SyncNowAsync(CancellationToken.None); // initial sync (history)

        Assert.Equal(1, client.GetTicketsWithHistoryCount);

        await monitor.SyncNowAsync(forceHistory: true, CancellationToken.None);

        Assert.Equal(2, client.GetTicketsWithHistoryCount);
    }

    [Fact]
    public async Task RunAsync_PausesPollingWhileUserIsActive()
    {
        var client = new FakeFtmsClient(null, []);
        var settings = new AppSettings { PollIntervalSeconds = 1 };
        var monitor = new TicketMonitor(client, new MemoryStore(), new NullSender(), new TicketChangeDetector(), settings);

        using var cts = new CancellationTokenSource();
        var userIsActive = true;
        var runTask = monitor.RunAsync(() => userIsActive, cts.Token);

        await Task.Delay(150);
        Assert.Equal(0, client.GetTicketsCallCount);

        userIsActive = false;
        await Task.Delay(1100);
        Assert.True(client.GetTicketsCallCount > 0);

        cts.Cancel();
        try { await runTask; } catch (OperationCanceledException) { }
    }

    private static TicketSnapshot Closed(string code, DateTimeOffset closedAt, long? closedById,
        string? closedByName, long? assigneeId) => new()
    {
        Code = code,
        Status = TicketStatus.Closed,
        UpdatedAt = closedAt,
        ClosedAt = closedAt,
        ClosedByUserId = closedById,
        ClosedByName = closedByName,
        AssigneeId = assigneeId,
        IsTerminal = true
    };

    private static TicketMonitor CreateMonitor(IFtmsClient client) => new(client, new MemoryStore(),
        new NullSender(), new TicketChangeDetector(), new AppSettings());

    private sealed class FakeFtmsClient(CurrentUserIdentity? user, IReadOnlyList<TicketSnapshot> tickets,
        IReadOnlyList<TicketSnapshot>? closedTickets = null) : IFtmsClient
    {
        public int GetTicketsCallCount { get; private set; }
        public int GetTicketsWithHistoryCount { get; private set; }
        public int GetTicketsActiveOnlyCount { get; private set; }
        public int GetClosedTicketsCallCount { get; private set; }
        public IReadOnlyList<TicketSnapshot> CurrentTickets { get; set; } = tickets;
        public IReadOnlyList<TicketSnapshot> CurrentClosedTickets { get; set; } = closedTickets ?? [];

        public Task<bool> IsAuthenticatedAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<CurrentUserIdentity?> GetCurrentUserAsync(CancellationToken cancellationToken) => Task.FromResult(user);

        public Task<IReadOnlyList<TicketSnapshot>> GetTicketsAsync(CancellationToken cancellationToken) =>
            GetTicketsAsync(includeHistory: true, cancellationToken);

        public Task<IReadOnlyList<TicketSnapshot>> GetTicketsAsync(bool includeHistory, CancellationToken cancellationToken)
        {
            GetTicketsCallCount++;
            if (includeHistory)
            {
                GetTicketsWithHistoryCount++;
                var merged = CurrentTickets.Concat(CurrentClosedTickets).ToList();
                return Task.FromResult<IReadOnlyList<TicketSnapshot>>(merged);
            }
            GetTicketsActiveOnlyCount++;
            return Task.FromResult(CurrentTickets);
        }

        public Task<IReadOnlyList<TicketSnapshot>> GetClosedTicketsAsync(CancellationToken cancellationToken)
        {
            GetClosedTicketsCallCount++;
            return Task.FromResult(CurrentClosedTickets);
        }

        public Task<TicketClaimResult> ClaimTicketAsync(string ticketCode, long expectedUserId, CancellationToken cancellationToken) =>
            Task.FromResult(new TicketClaimResult(TicketClaimStatus.Claimed, "ok"));
        public Task<LatestEmail?> GetLatestEmailAsync(string ticketCode, CancellationToken cancellationToken) => Task.FromResult<LatestEmail?>(null);
        public Task<StatusHistoryEntry?> GetLatestStatusHistoryAsync(string ticketCode, TicketStatus status, CancellationToken cancellationToken) =>
            Task.FromResult<StatusHistoryEntry?>(null);
        public Task BeginLoginRecoveryAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class MemoryStore : ITicketStore
    {
        public int CleanupCallCount { get; private set; }
        public List<TicketEvent> SavedEvents { get; } = [];
        public List<TicketSnapshot> SavedSnapshots { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyDictionary<string, TicketSnapshot>> LoadActiveSnapshotsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, TicketSnapshot>>(new Dictionary<string, TicketSnapshot>());
        public Task SaveSnapshotAsync(TicketSnapshot snapshot, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveSnapshotsAsync(IReadOnlyList<TicketSnapshot> snapshots, CancellationToken cancellationToken)
        {
            SavedSnapshots.AddRange(snapshots);
            return Task.CompletedTask;
        }
        public Task SaveEventAsync(TicketEvent ticketEvent, CancellationToken cancellationToken)
        {
            SavedEvents.Add(ticketEvent);
            return Task.CompletedTask;
        }
        public Task<bool> EventExistsAsync(string eventKey, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task EnqueueNotificationAsync(TicketEvent ticketEvent, string message, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveEventAndEnqueueNotificationAsync(TicketEvent ticketEvent, string? message, CancellationToken cancellationToken)
        {
            SavedEvents.Add(ticketEvent);
            return Task.CompletedTask;
        }
        public Task MarkTerminalAsync(string code, DateTimeOffset terminalAt, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> HasNotificationAsync(string ticketCode, string notificationType, string discriminator, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task RecordNotificationAsync(string ticketCode, string notificationType, string discriminator, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CleanupAsync(int retentionDays, CancellationToken cancellationToken)
        {
            CleanupCallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class NullSender : INotificationSender
    {
        public Task SendPendingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
