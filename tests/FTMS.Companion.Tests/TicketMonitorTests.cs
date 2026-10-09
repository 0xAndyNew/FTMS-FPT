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
    public async Task Poll_WhenActiveTicketClosesInHistory_PreservesActiveMetadataAndEmail()
    {
        var user = new CurrentUserIdentity(42, "AnhHVN3", null, null);
        var createdAt = new DateTimeOffset(2026, 10, 7, 16, 48, 0, TimeSpan.FromHours(7));
        var email = new LatestEmail("202610070240", createdAt.AddMinutes(5), "customer@unilever.com",
            "Tiêu đề", "Nội dung email");
        var activeTicket = new TicketSnapshot
        {
            Code = "RQ202610070240",
            Status = TicketStatus.InProgress,
            AssigneeId = 42,
            AssigneeName = "AnhHVN3 - TOC - Phòng Dịch vụ Data Center",
            DepartmentName = "TOC - Phòng Dịch vụ Data Center",
            CreatedAt = createdAt,
            UpdatedAt = createdAt.AddMinutes(10),
            LatestEmail = email
        };
        var client = new FakeFtmsClient(user, [activeTicket], []);
        var store = new MemoryStore();
        var monitor = new TicketMonitor(client, store, new NullSender(), new TicketChangeDetector(), new AppSettings());

        await monitor.InitializeAsync(CancellationToken.None);
        await monitor.SyncNowAsync(CancellationToken.None);

        // Next poll: ticket is now Closed in history with null assignee and null email
        var rawClosedFromHistory = new TicketSnapshot
        {
            Code = "RQ202610070240",
            Status = TicketStatus.Closed,
            AssigneeId = null,
            AssigneeName = null, // raw history has null
            CreatedAt = new DateTimeOffset(2026, 7, 10, 16, 48, 0, TimeSpan.FromHours(7)), // inverted US date
            UpdatedAt = createdAt.AddMinutes(30),
            ClosedAt = createdAt.AddMinutes(30),
            ClosedByName = "AnhHVN3",
            LatestEmail = null // raw history has null
        };
        client.CurrentTickets = [rawClosedFromHistory];

        await monitor.SyncNowAsync(forceHistory: true, CancellationToken.None);

        var terminalEvent = Assert.Single(store.SavedEvents, e => e.TicketCode == "RQ202610070240" && e.EventType == TicketEventType.Terminal);
        Assert.Equal("AnhHVN3 - TOC - Phòng Dịch vụ Data Center", terminalEvent.Snapshot.AssigneeName);
        Assert.Equal(createdAt, terminalEvent.Snapshot.CreatedAt);
        Assert.NotNull(terminalEvent.Snapshot.LatestEmail);
        Assert.Equal("202610070240", terminalEvent.Snapshot.LatestEmail.Id);
    }

    [Fact]
    public async Task Poll_TransientEmailFailure_DoesNotPersistCreatedUntilNextPollSucceeds()
    {
        var now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7));
        var ticket = new TicketSnapshot
        {
            Code = "RQ-CREATED-RECOVERY",
            Status = TicketStatus.New,
            Title = "Tiêu đề kiểm thử",
            CreatedAt = now,
            UpdatedAt = now,
            DepartmentName = "TOC - Phòng Dịch vụ Data Center",
            AssigneeId = null,
            AssigneeName = "---"
        };
        var email = new LatestEmail("201", now, "customer@example.com",
            ticket.Title, "Nội dung email sau khi FTMS phục hồi");
        var client = new FakeFtmsClient(null, [ticket])
        {
            LatestEmailByCall = (call, _) => call <= 2
                ? Task.FromException<LatestEmail?>(new InvalidOperationException("FTMS email API unavailable"))
                : Task.FromResult<LatestEmail?>(email)
        };
        var store = new MemoryStore();
        var monitor = new TicketMonitor(client, store, new NullSender(),
            new TicketChangeDetector(), new AppSettings());

        await monitor.InitializeAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            monitor.SyncNowAsync(CancellationToken.None));

        Assert.Empty(store.SavedEvents);
        Assert.Empty(store.SavedMessages);
        Assert.DoesNotContain(store.SavedSnapshots, item => item.Code == ticket.Code);

        await monitor.SyncNowAsync(CancellationToken.None);

        var createdEvent = Assert.Single(store.SavedEvents, item =>
            item.TicketCode == ticket.Code && item.EventType == TicketEventType.Created);
        Assert.Equal(email.Body, createdEvent.LatestEmail?.Body);
        var message = Assert.Single(store.SavedMessages);
        Assert.Contains(email.Body!, message);
    }

    [Fact]
    public async Task Poll_NullListEmail_PreservesTrackedEmailAndResponseReminderState()
    {
        var now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7));
        var email = new LatestEmail("301", now.AddMinutes(-1), "customer@example.com",
            "Tiêu đề kiểm thử", "Nội dung email đã lưu");
        var tracked = new TicketSnapshot
        {
            Code = "RQ-PRESERVE-EMAIL",
            Status = TicketStatus.InProgress,
            Title = email.Subject,
            CreatedAt = now.AddHours(-1),
            UpdatedAt = now,
            DepartmentName = "TOC - Phòng Dịch vụ Data Center",
            AssigneeId = 42,
            AssigneeName = "closer.user",
            LatestEmail = email,
            ResponseReminderEmailId = email.Id,
            ResponseReminderSince = now.AddMinutes(-1)
        };
        var rawListTicket = tracked with
        {
            LatestEmail = null,
            ResponseReminderEmailId = null,
            ResponseReminderSince = null
        };
        var client = new FakeFtmsClient(
            new CurrentUserIdentity(42, "closer.user", null, null), [rawListTicket]);
        var store = new MemoryStore();
        store.InitialSnapshots[tracked.Code] = tracked;
        var monitor = new TicketMonitor(client, store, new NullSender(),
            new TicketChangeDetector(), new AppSettings());

        await monitor.InitializeAsync(CancellationToken.None);
        await monitor.SyncNowAsync(CancellationToken.None);

        var preserved = Assert.Single(monitor.GetActiveSnapshots(), item => item.Code == tracked.Code);
        Assert.Equal(email.Body, preserved.LatestEmail?.Body);
        Assert.Equal(email.Id, preserved.ResponseReminderEmailId);
        Assert.Equal(tracked.ResponseReminderSince, preserved.ResponseReminderSince);
        Assert.Equal(0, client.GetLatestEmailCallCount);
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
    public async Task RunAsync_PollsContinuouslyIndependentOfVisibleUserActivity()
    {
        var client = new FakeFtmsClient(null, []);
        var settings = new AppSettings { PollIntervalSeconds = 1 };
        var monitor = new TicketMonitor(client, new MemoryStore(), new NullSender(), new TicketChangeDetector(), settings);

        using var cts = new CancellationTokenSource();
        var runTask = monitor.RunAsync(cts.Token);

        await Task.Delay(150);
        Assert.True(client.GetTicketsCallCount > 0);

        cts.Cancel();
        try { await runTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task SyncUntilAssignment_RetriesUntilExpectedAssigneeAppears()
    {
        var unassigned = new TicketSnapshot
        {
            Code = "RQ-ASSIGNMENT-SYNC",
            Status = TicketStatus.InProgress,
            AssigneeId = null,
            AssigneeName = "---",
            DepartmentName = "TOC - Phòng Dịch vụ Data Center"
        };
        var assigned = unassigned with { AssigneeId = 42, AssigneeName = "hieu.user" };
        var client = new FakeFtmsClient(new CurrentUserIdentity(42, "hieu.user", null, null), [unassigned]);
        var store = new MemoryStore();
        var sender = new NullSender();
        var monitor = new TicketMonitor(client, store, sender, new TicketChangeDetector(), new AppSettings());

        await monitor.InitializeAsync(CancellationToken.None);
        await monitor.SyncNowAsync(CancellationToken.None);
        client.TicketsByCall = call => call >= 3 ? [assigned] : [unassigned];

        await monitor.SyncUntilAssignmentAsync(unassigned.Code, 42, CancellationToken.None);

        Assert.Equal(3, client.GetTicketsCallCount);
        var assignmentEvent = Assert.Single(store.SavedEvents);
        Assert.Equal(TicketEventType.AssignmentChanged, assignmentEvent.EventType);
        Assert.Equal(42, assignmentEvent.Snapshot.AssigneeId);
        Assert.True(sender.SendPendingCallCount >= 1);
    }

    [Fact]
    public async Task ExistingPausedTicketIsBaselinedWithoutCreatedAlertAndEmitsStatusChangedWhenResumed()
    {
        var user = new CurrentUserIdentity(42, "hieu.user", null, null);
        var initialPaused = new TicketSnapshot
        {
            Code = "RQ-EXISTING",
            Status = TicketStatus.Paused,
            DepartmentName = "TOC - Phòng Dịch vụ Data Center",
            AssigneeId = 42,
            AssigneeName = "hieu.user",
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        };
        var client = new FakeFtmsClient(user, [initialPaused], []);
        var store = new MemoryStore();
        var monitor = new TicketMonitor(client, store, new NullSender(), new TicketChangeDetector(), new AppSettings());

        await monitor.InitializeAsync(CancellationToken.None);
        await monitor.SyncNowAsync(CancellationToken.None);

        Assert.DoesNotContain(store.SavedEvents, e => e.TicketCode == "RQ-EXISTING");

        var resumedInProgress = initialPaused with
        {
            Status = TicketStatus.InProgress,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        client.CurrentTickets = [resumedInProgress];

        await monitor.SyncNowAsync(CancellationToken.None);

        var transition = Assert.Single(store.SavedEvents, e => e.TicketCode == "RQ-EXISTING");
        Assert.Equal(TicketEventType.StatusChanged, transition.EventType);
        Assert.Equal(TicketStatus.Paused, transition.PreviousStatus);
        Assert.Equal(TicketStatus.InProgress, transition.CurrentStatus);
    }

    [Fact]
    public async Task ApplyConfirmedStatusMutation_ImmediatelyEnqueuesEventAndPreventsRollback()
    {
        var ticket = new TicketSnapshot
        {
            Code = "RQ-MUT-STATUS",
            Status = TicketStatus.InProgress,
            DepartmentName = "TOC - Phòng Dịch vụ Data Center",
            AssigneeId = 42,
            AssigneeName = "duy.user"
        };
        var client = new FakeFtmsClient(new CurrentUserIdentity(42, "duy.user", null, null), [ticket]);
        var store = new MemoryStore();
        var sender = new NullSender();
        var monitor = new TicketMonitor(client, store, sender, new TicketChangeDetector(), new AppSettings());

        await monitor.InitializeAsync(CancellationToken.None);
        await monitor.SyncNowAsync(CancellationToken.None);
        var initialSendCount = sender.SendPendingCallCount;

        // Technician changes status to Paused via browser
        await monitor.ApplyConfirmedStatusMutationAsync(ticket.Code, TicketStatus.Paused, TicketStatus.InProgress, "duy.user", CancellationToken.None);

        Assert.True(monitor.TryGetTrackedStatus(ticket.Code, out var currentStatus));
        Assert.Equal(TicketStatus.Paused, currentStatus);
        Assert.True(sender.SendPendingCallCount > initialSendCount);
        var statusEvent = Assert.Single(store.SavedEvents, e => e.TicketCode == ticket.Code && e.EventType == TicketEventType.StatusChanged);
        Assert.Equal(TicketStatus.Paused, statusEvent.CurrentStatus);
        Assert.Equal(TicketStatus.InProgress, statusEvent.PreviousStatus);
        Assert.Equal("duy.user", statusEvent.ChangedBy);

        // Subsequent poll where FTMS API still returns old status (eventual consistency)
        await monitor.SyncNowAsync(CancellationToken.None);

        // Must NOT roll back to InProgress and must NOT emit duplicate events
        Assert.True(monitor.TryGetTrackedStatus(ticket.Code, out var preservedStatus));
        Assert.Equal(TicketStatus.Paused, preservedStatus);
        Assert.Single(store.SavedEvents, e => e.TicketCode == ticket.Code && e.EventType == TicketEventType.StatusChanged);
    }

    [Fact]
    public async Task ApplyConfirmedStatusMutation_EmitsImmediateStatusAndEnrichesEmailOnNextSync()
    {
        var sentAt = DateTimeOffset.UtcNow;
        var oldEmail = new LatestEmail("100", sentAt.AddMinutes(-38), "TrungNT105@fpt.com",
            "Tiêu đề kiểm thử", "Nội dung phản hồi cũ");
        var newEmail = new LatestEmail("101", sentAt, "tunglamtu94@gmail.com",
            "Tiêu đề kiểm thử", "Nội dung phản hồi mới nhất");
        var ticket = new TicketSnapshot
        {
            Code = "RQ202610070261",
            Status = TicketStatus.Paused,
            DepartmentName = "TOC - Phòng Dịch vụ Data Center",
            AssigneeId = 42,
            AssigneeName = "HieuDX2",
            LatestEmail = oldEmail
        };
        var client = new FakeFtmsClient(new CurrentUserIdentity(42, "HieuDX2", null, null), [ticket])
        {
            LatestEmail = newEmail
        };
        var store = new MemoryStore();
        var monitor = new TicketMonitor(client, store, new NullSender(), new TicketChangeDetector(), new AppSettings());

        await monitor.InitializeAsync(CancellationToken.None);
        await monitor.SyncNowAsync(CancellationToken.None);
        await monitor.ApplyConfirmedStatusMutationAsync(ticket.Code, TicketStatus.InProgress,
            TicketStatus.Paused, "HieuDX2", CancellationToken.None);

        var statusEvent = Assert.Single(store.SavedEvents, e =>
            e.TicketCode == ticket.Code && e.EventType == TicketEventType.StatusChanged);
        Assert.Equal("101", statusEvent.LatestEmail?.Id);
        Assert.Equal(newEmail.Body, statusEvent.LatestEmail?.Body);

        // The latest email is included in the status notification, so the next sync must not duplicate it.
        await monitor.SyncNowAsync(CancellationToken.None);
        Assert.DoesNotContain(store.SavedEvents, e =>
            e.TicketCode == ticket.Code && e.EventType == TicketEventType.EmailReceived);
    }

    [Fact]
    public async Task ApplyConfirmedAssignmentMutation_ImmediatelyEnqueuesAssignmentEventAndPreventsRollback()
    {
        var ticket = new TicketSnapshot
        {
            Code = "RQ-MUT-CLAIM",
            Status = TicketStatus.Assigned,
            DepartmentName = "TOC - Phòng Dịch vụ Data Center",
            AssigneeId = null,
            AssigneeName = "---"
        };
        var client = new FakeFtmsClient(new CurrentUserIdentity(42, "duy.user", null, null), [ticket]);
        var store = new MemoryStore();
        var sender = new NullSender();
        var monitor = new TicketMonitor(client, store, sender, new TicketChangeDetector(), new AppSettings());

        await monitor.InitializeAsync(CancellationToken.None);
        await monitor.SyncNowAsync(CancellationToken.None);
        var initialSendCount = sender.SendPendingCallCount;

        // Technician claims ticket in browser
        await monitor.ApplyConfirmedAssignmentMutationAsync(ticket.Code, 42, "duy.user", "duy.user", TicketStatus.InProgress, CancellationToken.None);

        Assert.True(monitor.TryGetTrackedStatus(ticket.Code, out var currentStatus));
        Assert.Equal(TicketStatus.InProgress, currentStatus);
        Assert.True(sender.SendPendingCallCount > initialSendCount);
        var claimEvent = Assert.Single(store.SavedEvents, e => e.TicketCode == ticket.Code && e.EventType == TicketEventType.AssignmentChanged);
        Assert.Equal(42, claimEvent.Snapshot.AssigneeId);
        Assert.Equal("duy.user", claimEvent.Snapshot.AssigneeName);
        Assert.Equal(TicketStatus.InProgress, claimEvent.CurrentStatus);

        // Eventual consistency poll where FTMS API still returns unassigned
        await monitor.SyncNowAsync(CancellationToken.None);

        // Must NOT roll back
        Assert.Single(store.SavedEvents, e => e.TicketCode == ticket.Code && e.EventType == TicketEventType.AssignmentChanged);
    }

    [Fact]
    public async Task TriggerSync_WakesUpRunAsyncImmediatelyWithoutWaitingInterval()
    {
        var client = new FakeFtmsClient(null, []);
        var settings = new AppSettings { PollIntervalSeconds = 60 };
        var monitor = new TicketMonitor(client, new MemoryStore(), new NullSender(), new TicketChangeDetector(), settings);

        using var cts = new CancellationTokenSource();
        var runTask = monitor.RunAsync(cts.Token);

        // Wait for first poll to complete
        while (client.GetTicketsCallCount == 0)
            await Task.Delay(20);

        var countBeforeTrigger = client.GetTicketsCallCount;

        // Trigger sync should wake RunAsync within milliseconds instead of 60s
        monitor.TriggerSync();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (client.GetTicketsCallCount == countBeforeTrigger && sw.ElapsedMilliseconds < 2000)
            await Task.Delay(20);

        Assert.True(client.GetTicketsCallCount > countBeforeTrigger);

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
        public Func<int, IReadOnlyList<TicketSnapshot>>? TicketsByCall { get; set; }
        public LatestEmail? LatestEmail { get; set; }
        public Func<int, CancellationToken, Task<LatestEmail?>>? LatestEmailByCall { get; set; }
        public int GetLatestEmailCallCount { get; private set; }

        public Task<bool> IsAuthenticatedAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<CurrentUserIdentity?> GetCurrentUserAsync(CancellationToken cancellationToken) => Task.FromResult(user);

        public Task<IReadOnlyList<TicketSnapshot>> GetTicketsAsync(CancellationToken cancellationToken) =>
            GetTicketsAsync(includeHistory: true, cancellationToken);

        public Task<IReadOnlyList<TicketSnapshot>> GetTicketsAsync(bool includeHistory, CancellationToken cancellationToken)
        {
            GetTicketsCallCount++;
            var currentTickets = TicketsByCall?.Invoke(GetTicketsCallCount) ?? CurrentTickets;
            if (includeHistory)
            {
                GetTicketsWithHistoryCount++;
                var merged = currentTickets.Concat(CurrentClosedTickets).ToList();
                return Task.FromResult<IReadOnlyList<TicketSnapshot>>(merged);
            }
            GetTicketsActiveOnlyCount++;
            return Task.FromResult(currentTickets);
        }

        public Task<IReadOnlyList<TicketSnapshot>> GetClosedTicketsAsync(CancellationToken cancellationToken)
        {
            GetClosedTicketsCallCount++;
            return Task.FromResult(CurrentClosedTickets);
        }

        public Task<TicketClaimResult> ClaimTicketAsync(string ticketCode, long expectedUserId, CancellationToken cancellationToken) =>
            Task.FromResult(new TicketClaimResult(TicketClaimStatus.Claimed, "ok"));
        public Task<LatestEmail?> GetLatestEmailAsync(string ticketCode, CancellationToken cancellationToken)
        {
            GetLatestEmailCallCount++;
            return LatestEmailByCall?.Invoke(GetLatestEmailCallCount, cancellationToken) ??
                Task.FromResult(LatestEmail);
        }
        public Task<StatusHistoryEntry?> GetLatestStatusHistoryAsync(string ticketCode, TicketStatus status, CancellationToken cancellationToken) =>
            Task.FromResult<StatusHistoryEntry?>(null);
        public Task BeginLoginRecoveryAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class MemoryStore : ITicketStore
    {
        public int CleanupCallCount { get; private set; }
        public Dictionary<string, TicketSnapshot> InitialSnapshots { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public List<TicketEvent> SavedEvents { get; } = [];
        public List<TicketSnapshot> SavedSnapshots { get; } = [];
        public List<string> SavedMessages { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyDictionary<string, TicketSnapshot>> LoadActiveSnapshotsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, TicketSnapshot>>(
                new Dictionary<string, TicketSnapshot>(InitialSnapshots, StringComparer.OrdinalIgnoreCase));
        public Task SaveSnapshotAsync(TicketSnapshot snapshot, CancellationToken cancellationToken)
        {
            SavedSnapshots.Add(snapshot);
            return Task.CompletedTask;
        }
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
            if (!string.IsNullOrWhiteSpace(message)) SavedMessages.Add(message);
            return Task.CompletedTask;
        }
        public Task SaveSnapshotAndEventsAsync(TicketSnapshot snapshot,
            IReadOnlyList<(TicketEvent Event, string? Message)> events, CancellationToken cancellationToken)
        {
            SavedSnapshots.Add(snapshot);
            SavedEvents.AddRange(events.Select(item => item.Event));
            SavedMessages.AddRange(events.Where(item => !string.IsNullOrWhiteSpace(item.Message))
                .Select(item => item.Message!));
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
        public int SendPendingCallCount { get; private set; }

        public void Signal() => SendPendingCallCount++;

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
    }
}
