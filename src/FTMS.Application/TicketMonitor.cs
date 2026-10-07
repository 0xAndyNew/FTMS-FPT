using System.Security.Cryptography;
using System.Text;
using FTMS.Domain;

namespace FTMS.Application;

public sealed class TicketMonitor(IFtmsClient client, ITicketStore store, INotificationSender sender,
    TicketChangeDetector detector, AppSettings settings)
{
    private readonly Dictionary<string, TicketSnapshot> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TicketSnapshot> _closedCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _missingMonitoredAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _terminalTombstones = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (TicketStatus Status, DateTimeOffset Timestamp)> _recentStatusMutations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long AssigneeId, string? AssigneeName, DateTimeOffset Timestamp)> _recentAssignmentMutations = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private TaskCompletionSource<bool> _wakeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _pendingSync;
    private DateTimeOffset _lastHistoryFetch = DateTimeOffset.MinValue;
    private bool _forceHistoryNext = true;
    private DateTimeOffset _lastCleanupTime = DateTimeOffset.MinValue;
    private int _lastCleanupDay = -1;
    public event Action<string>? StatusChanged;
    public event Action<DashboardSummary>? SummaryChanged;
    public event Action? DailyCleanupCompleted;
    public DateTimeOffset LastCleanupTime => _lastCleanupTime;
    public int LastCleanupDay => _lastCleanupDay;

    public bool TryGetTrackedStatus(string code, out TicketStatus status)
    {
        lock (_active)
        {
            if (_active.TryGetValue(code, out var snapshot))
            {
                status = snapshot.Status;
                return true;
            }
            if (_closedCache.TryGetValue(code, out var closedSnapshot))
            {
                status = closedSnapshot.Status;
                return true;
            }
        }
        status = default;
        return false;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await store.InitializeAsync(cancellationToken);
        foreach (var item in await store.LoadActiveSnapshotsAsync(cancellationToken)) _active[item.Key] = item.Value;
        await CheckAndRunDailyCleanupAsync(cancellationToken);
    }

    public void TriggerSync(bool forceHistory = false)
    {
        if (forceHistory) _forceHistoryNext = true;
        Interlocked.Exchange(ref _pendingSync, 1);
        _wakeSignal.TrySetResult(true);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var senderTask = sender.RunAsync(cancellationToken);
        sender.Signal();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (!await client.IsAuthenticatedAsync(cancellationToken))
                    {
                        SummaryChanged?.Invoke(UnavailableSummary());
                        await client.BeginLoginRecoveryAsync(cancellationToken);
                    }
                    else
                    {
                        await SyncNowAsync(cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (UnauthorizedAccessException)
                {
                    SummaryChanged?.Invoke(UnavailableSummary());
                    await client.BeginLoginRecoveryAsync(cancellationToken);
                }
                catch (Exception ex) { StatusChanged?.Invoke($"Loi: {ex.Message}"); }
                try
                {
                    var wakeTask = _wakeSignal.Task;
                    var delayTask = Task.Delay(TimeSpan.FromSeconds(Math.Max(1, settings.PollIntervalSeconds)), cancellationToken);
                    await Task.WhenAny(wakeTask, delayTask);
                    _wakeSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            }
        }
        finally
        {
            try { await senderTask; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
    }

    public Task SyncNowAsync(CancellationToken cancellationToken) =>
        SyncNowAsync(forceHistory: false, cancellationToken);

    public async Task SyncNowAsync(bool forceHistory, CancellationToken cancellationToken)
    {
        if (forceHistory) _forceHistoryNext = true;
        if (!await _syncLock.WaitAsync(0, cancellationToken))
        {
            Interlocked.Exchange(ref _pendingSync, 1);
            return;
        }
        try
        {
            do
            {
                Interlocked.Exchange(ref _pendingSync, 0);
                var includeHistory = _forceHistoryNext;
                _forceHistoryNext = false;
                await PollOnceAsync(includeHistory, cancellationToken);
            } while (Interlocked.CompareExchange(ref _pendingSync, 0, 0) == 1 && !cancellationToken.IsCancellationRequested);
        }
        finally { _syncLock.Release(); }
    }

    public async Task ApplyConfirmedStatusMutationAsync(string code, TicketStatus newStatus,
        TicketStatus? previousStatusHint, string? actor, CancellationToken cancellationToken,
        DateTimeOffset? detectedAt = null, string? note = null)
    {
        if (string.IsNullOrWhiteSpace(code)) return;

        var now = (detectedAt ?? DateTimeOffset.UtcNow).ToOffset(TimeSpan.FromHours(7));
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            _recentStatusMutations[code] = (newStatus, DateTimeOffset.UtcNow);
            TicketSnapshot? current;
            lock (_active) _active.TryGetValue(code, out current);
            if (current is null)
            {
                TriggerSync();
                return;
            }
            if (current.Status == newStatus) return;

            var email = await TicketChangeDetector.ResolveLatestEmailWithDeadlineAsync(client, code,
                cancellationToken, retryWhenMissing: false, current.LatestEmail);

            var previousStatus = current.Status;
            var isTerminal = newStatus.IsTerminal(settings.UnprocessedIsTerminal);
            var updated = current with
            {
                Status = newStatus,
                UpdatedAt = now,
                IsTerminal = isTerminal,
                ClosedAt = newStatus == TicketStatus.Closed ? now : current.ClosedAt,
                ClosedByName = newStatus == TicketStatus.Closed ? actor ?? current.ClosedByName : current.ClosedByName,
                LatestEmail = email ?? current.LatestEmail
            };
            if (isTerminal)
            {
                lock (_active) _terminalTombstones[code] = DateTimeOffset.UtcNow;
            }
            var eventType = isTerminal ? TicketEventType.Terminal : TicketEventType.StatusChanged;
            var raw = $"{code}|{eventType}|{previousStatus}|{newStatus}";
            var ticketEvent = new TicketEvent
            {
                EventKey = Hash(raw),
                TicketCode = code,
                EventType = eventType,
                PreviousStatus = previousStatus,
                CurrentStatus = newStatus,
                DetectedAt = now,
                Reason = !string.IsNullOrWhiteSpace(note) ? note : "Trạng thái ticket đã thay đổi",
                ChangedBy = actor,
                ChangedAt = now,
                LatestEmail = email ?? current.LatestEmail,
                Note = note,
                Snapshot = updated
            };
            var message = FormatIfNotifiable(ticketEvent, current);
            await store.SaveSnapshotAndEventsAsync(updated, [(ticketEvent, message)], cancellationToken);
            UpdateTrackedSnapshot(updated);
            sender.Signal();
        }
        finally { _stateLock.Release(); }

        TriggerSync();
    }

    public async Task ApplyConfirmedAssignmentMutationAsync(string code, long expectedAssigneeId,
        string? expectedAssigneeName, string? actor, TicketStatus? newStatusHint,
        CancellationToken cancellationToken, DateTimeOffset? detectedAt = null)
    {
        if (string.IsNullOrWhiteSpace(code) || expectedAssigneeId <= 0) return;

        var now = (detectedAt ?? DateTimeOffset.UtcNow).ToOffset(TimeSpan.FromHours(7));
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            _recentAssignmentMutations[code] = (expectedAssigneeId, expectedAssigneeName, DateTimeOffset.UtcNow);
            if (newStatusHint is not null)
                _recentStatusMutations[code] = (newStatusHint.Value, DateTimeOffset.UtcNow);
            TicketSnapshot? current;
            lock (_active) _active.TryGetValue(code, out current);
            if (current is null)
            {
                TriggerSync();
                return;
            }

            var effectiveStatus = newStatusHint ?? current.Status;
            if (current.AssigneeId == expectedAssigneeId && current.Status == effectiveStatus) return;

            var email = await TicketChangeDetector.ResolveLatestEmailWithDeadlineAsync(client, code,
                cancellationToken, retryWhenMissing: false, current.LatestEmail);

            var updated = current with
            {
                AssigneeId = expectedAssigneeId,
                AssigneeName = expectedAssigneeName ?? actor ?? current.AssigneeName,
                Status = effectiveStatus,
                UpdatedAt = now,
                IsTerminal = effectiveStatus.IsTerminal(settings.UnprocessedIsTerminal),
                LatestEmail = email ?? current.LatestEmail
            };
            var discriminator = $"{current.AssigneeId}>{expectedAssigneeId}|" +
                $"{current.DepartmentId}>{current.DepartmentId}|{current.Status}>{effectiveStatus}|{now:O}";
            var ticketEvent = new TicketEvent
            {
                EventKey = Hash($"{code}|{TicketEventType.AssignmentChanged}|{discriminator}"),
                TicketCode = code,
                EventType = TicketEventType.AssignmentChanged,
                PreviousStatus = current.Status,
                CurrentStatus = effectiveStatus,
                DetectedAt = now,
                Reason = "Người xử lý hoặc phòng ban đã thay đổi",
                ChangedBy = actor,
                ChangedAt = now,
                PreviousAssigneeName = current.AssigneeName,
                PreviousDepartmentName = current.DepartmentName,
                LatestEmail = email ?? current.LatestEmail,
                Snapshot = updated
            };
            var message = FormatIfNotifiable(ticketEvent, current);
            await store.SaveSnapshotAndEventsAsync(updated, [(ticketEvent, message)], cancellationToken);
            UpdateTrackedSnapshot(updated);
            sender.Signal();
        }
        finally { _stateLock.Release(); }

        TriggerSync();
    }

    private string? FormatIfNotifiable(TicketEvent item, TicketSnapshot? previous)
    {
        if (!TicketNotificationFilter.ShouldNotify(item, previous)) return null;
        var ihubBase = new Uri(settings.FtmsUrl).GetLeftPart(UriPartial.Authority) + "/ihub";
        return NotificationFormatter.Format(item, ihubBase);
    }

    private void UpdateTrackedSnapshot(TicketSnapshot snapshot)
    {
        lock (_active)
        {
            if (snapshot.IsTerminal)
            {
                _active.Remove(snapshot.Code);
                _missingMonitoredAttempts.Remove(snapshot.Code);
                _terminalTombstones[snapshot.Code] = DateTimeOffset.UtcNow;
                if (snapshot.Status == TicketStatus.Closed) _closedCache[snapshot.Code] = snapshot;
            }
            else
            {
                _terminalTombstones.Remove(snapshot.Code);
                _active[snapshot.Code] = snapshot;
            }
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public Task SyncUntilStatusAsync(string code, TicketStatus expectedStatus,
        CancellationToken cancellationToken) =>
        SyncUntilAsync(() => TryGetTrackedStatus(code, out var status) && status == expectedStatus,
            cancellationToken);

    public Task SyncUntilAssignmentAsync(string code, long expectedAssigneeId,
        CancellationToken cancellationToken) =>
        SyncUntilAsync(() =>
        {
            lock (_active)
            {
                return _active.TryGetValue(code, out var snapshot) && snapshot.AssigneeId == expectedAssigneeId;
            }
        }, cancellationToken);

    private async Task SyncUntilAsync(Func<bool> changeObserved, CancellationToken cancellationToken)
    {
        if (changeObserved()) return;
        foreach (var delay in new[] { 0, 500, 1500 })
        {
            if (delay > 0) await Task.Delay(delay, cancellationToken);
            await _syncLock.WaitAsync(cancellationToken);
            try
            {
                var includeHistory = _forceHistoryNext;
                _forceHistoryNext = false;
                await PollOnceAsync(includeHistory, cancellationToken);
            }
            finally { _syncLock.Release(); }
            if (changeObserved()) return;
        }
    }

    private async Task PollOnceAsync(bool forceHistoryUpfront, CancellationToken cancellationToken)
    {
        var historyDue = forceHistoryUpfront ||
            _lastHistoryFetch == DateTimeOffset.MinValue ||
            (DateTimeOffset.UtcNow - _lastHistoryFetch) >= TimeSpan.FromSeconds(Math.Max(10, settings.HistoryIntervalSeconds));

        var rawApiTickets = await client.GetTicketsAsync(includeHistory: historyDue, cancellationToken);
        var currentUser = await client.GetCurrentUserAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var apiTickets = new List<TicketSnapshot>(rawApiTickets.Count);
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var item in rawApiTickets)
            {
                lock (_active)
                {
                    if (_terminalTombstones.TryGetValue(item.Code, out var tombstoneTime))
                    {
                        if (now - tombstoneTime < TimeSpan.FromMinutes(10))
                        {
                            continue;
                        }
                        _terminalTombstones.Remove(item.Code);
                    }
                }
                var ticket = item;
                if (_recentStatusMutations.TryGetValue(ticket.Code, out var statusMutation))
                {
                    if (now - statusMutation.Timestamp < TimeSpan.FromSeconds(15))
                    {
                        if (ticket.Status != statusMutation.Status)
                        {
                            ticket = ticket with
                            {
                                Status = statusMutation.Status,
                                IsTerminal = statusMutation.Status.IsTerminal(settings.UnprocessedIsTerminal)
                            };
                        }
                    }
                    else
                    {
                        _recentStatusMutations.Remove(ticket.Code);
                    }
                }
                if (_recentAssignmentMutations.TryGetValue(ticket.Code, out var assignMutation))
                {
                    if (now - assignMutation.Timestamp < TimeSpan.FromSeconds(15))
                    {
                        if (ticket.AssigneeId != assignMutation.AssigneeId)
                        {
                            ticket = ticket with
                            {
                                AssigneeId = assignMutation.AssigneeId,
                                AssigneeName = assignMutation.AssigneeName ?? ticket.AssigneeName
                            };
                        }
                    }
                    else
                    {
                        _recentAssignmentMutations.Remove(ticket.Code);
                    }
                }
                apiTickets.Add(ticket);
            }
        }
        finally { _stateLock.Release(); }

        if (historyDue)
        {
            _lastHistoryFetch = DateTimeOffset.UtcNow;
            foreach (var item in apiTickets.Where(x => x.Status == TicketStatus.Closed))
            {
                if (_active.TryGetValue(item.Code, out var existingActive))
                {
                    _closedCache[item.Code] = existingActive with
                    {
                        Status = item.Status,
                        UpdatedAt = item.UpdatedAt ?? item.ClosedAt ?? existingActive.UpdatedAt,
                        ClosedAt = item.ClosedAt ?? item.UpdatedAt ?? existingActive.UpdatedAt,
                        ClosedByName = item.ClosedByName ?? item.UpdatedBy ?? existingActive.ClosedByName,
                        ClosedByUserId = item.ClosedByUserId ?? existingActive.ClosedByUserId,
                        AssigneeId = (item.AssigneeId is not null and not 0) ? item.AssigneeId : existingActive.AssigneeId,
                        AssigneeName = (!string.IsNullOrWhiteSpace(item.AssigneeName) && item.AssigneeName.Trim() != "---" && item.AssigneeName.Trim() != "Chưa nhận")
                            ? item.AssigneeName : existingActive.AssigneeName,
                        CreatedAt = existingActive.CreatedAt ?? item.CreatedAt,
                        DepartmentId = item.DepartmentId ?? existingActive.DepartmentId,
                        DepartmentName = !string.IsNullOrWhiteSpace(item.DepartmentName) ? item.DepartmentName : existingActive.DepartmentName,
                        Title = !string.IsNullOrWhiteSpace(existingActive.Title) ? existingActive.Title : item.Title,
                        LatestEmail = TicketChangeDetector.SelectEmail(item.LatestEmail, existingActive.LatestEmail),
                        IsTerminal = true
                    };
                }
                else
                {
                    _closedCache[item.Code] = item;
                }
            }
        }

        var activeCodes = new HashSet<string>(apiTickets.Select(x => x.Code), StringComparer.OrdinalIgnoreCase);
        var missingMonitored = _active.Keys.Where(code => !activeCodes.Contains(code)).ToList();

        if (missingMonitored.Count > 0 && !historyDue)
        {
            var needHistoryProbe = missingMonitored.Any(code =>
                !_missingMonitoredAttempts.TryGetValue(code, out var attempts) || attempts <= 2);

            if (needHistoryProbe)
            {
                var closedTickets = await client.GetClosedTicketsAsync(cancellationToken);
                _lastHistoryFetch = DateTimeOffset.UtcNow;
                foreach (var item in closedTickets)
                {
                    if (_active.TryGetValue(item.Code, out var existingActive))
                    {
                        _closedCache[item.Code] = existingActive with
                        {
                            Status = item.Status,
                            UpdatedAt = item.UpdatedAt ?? item.ClosedAt ?? existingActive.UpdatedAt,
                            ClosedAt = item.ClosedAt ?? item.UpdatedAt ?? existingActive.UpdatedAt,
                            ClosedByName = item.ClosedByName ?? item.UpdatedBy ?? existingActive.ClosedByName,
                            ClosedByUserId = item.ClosedByUserId ?? existingActive.ClosedByUserId,
                            AssigneeId = (item.AssigneeId is not null and not 0) ? item.AssigneeId : existingActive.AssigneeId,
                            AssigneeName = (!string.IsNullOrWhiteSpace(item.AssigneeName) && item.AssigneeName.Trim() != "---" && item.AssigneeName.Trim() != "Chưa nhận")
                                ? item.AssigneeName : existingActive.AssigneeName,
                            CreatedAt = existingActive.CreatedAt ?? item.CreatedAt,
                            DepartmentId = item.DepartmentId ?? existingActive.DepartmentId,
                            DepartmentName = !string.IsNullOrWhiteSpace(item.DepartmentName) ? item.DepartmentName : existingActive.DepartmentName,
                            Title = !string.IsNullOrWhiteSpace(existingActive.Title) ? existingActive.Title : item.Title,
                            LatestEmail = TicketChangeDetector.SelectEmail(item.LatestEmail, existingActive.LatestEmail),
                            IsTerminal = true
                        };
                    }
                    else
                    {
                        _closedCache[item.Code] = item;
                    }
                }
            }

            foreach (var code in missingMonitored)
            {
                _missingMonitoredAttempts[code] = _missingMonitoredAttempts.TryGetValue(code, out var count) ? count + 1 : 1;
            }
        }

        foreach (var code in activeCodes)
        {
            _missingMonitoredAttempts.Remove(code);
            // Xóa closed cache khi ticket active lại (không phải terminal), tránh stale history ghi đè
            var activeTicket = apiTickets.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));
            var isTombstoned = false;
            lock (_active) isTombstoned = _terminalTombstones.ContainsKey(code);
            if (activeTicket is not null && !activeTicket.Status.IsTerminal(settings.UnprocessedIsTerminal) && !isTombstoned)
                _closedCache.Remove(code);
        }

        // Closed history is only relevant when it closes a ticket already being monitored.
        var tickets = new List<TicketSnapshot>();
        foreach (var item in apiTickets)
        {
            if (!item.Status.IsTerminal(settings.UnprocessedIsTerminal))
            {
                tickets.Add(item);
            }
            else if (_active.TryGetValue(item.Code, out var existingActive))
            {
                var closedTicket = existingActive with
                {
                    Status = item.Status,
                    UpdatedAt = item.UpdatedAt ?? item.ClosedAt ?? existingActive.UpdatedAt,
                    ClosedAt = item.ClosedAt ?? item.UpdatedAt ?? existingActive.UpdatedAt,
                    ClosedByName = item.ClosedByName ?? item.UpdatedBy ?? existingActive.ClosedByName,
                    ClosedByUserId = item.ClosedByUserId ?? existingActive.ClosedByUserId,
                    AssigneeId = (item.AssigneeId is not null and not 0) ? item.AssigneeId : existingActive.AssigneeId,
                    AssigneeName = (!string.IsNullOrWhiteSpace(item.AssigneeName) && item.AssigneeName.Trim() != "---" && item.AssigneeName.Trim() != "Chưa nhận")
                        ? item.AssigneeName : existingActive.AssigneeName,
                    CreatedAt = existingActive.CreatedAt ?? item.CreatedAt,
                    DepartmentId = item.DepartmentId ?? existingActive.DepartmentId,
                    DepartmentName = !string.IsNullOrWhiteSpace(item.DepartmentName) ? item.DepartmentName : existingActive.DepartmentName,
                    Title = !string.IsNullOrWhiteSpace(existingActive.Title) ? existingActive.Title : item.Title,
                    LatestEmail = TicketChangeDetector.SelectEmail(item.LatestEmail, existingActive.LatestEmail),
                    IsTerminal = true
                };
                tickets.Add(closedTicket);
            }
        }

        foreach (var activeCode in _active.Keys)
        {
            if (tickets.Any(x => string.Equals(x.Code, activeCode, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (_closedCache.TryGetValue(activeCode, out var closedSnapshot))
            {
                if (_active.TryGetValue(activeCode, out var existingActive))
                {
                    var closedTicket = existingActive with
                    {
                        Status = TicketStatus.Closed,
                        UpdatedAt = closedSnapshot.UpdatedAt ?? closedSnapshot.ClosedAt ?? existingActive.UpdatedAt,
                        ClosedAt = closedSnapshot.ClosedAt ?? closedSnapshot.UpdatedAt ?? existingActive.UpdatedAt,
                        ClosedByName = closedSnapshot.ClosedByName ?? existingActive.ClosedByName,
                        ClosedByUserId = closedSnapshot.ClosedByUserId ?? existingActive.ClosedByUserId,
                        AssigneeId = (closedSnapshot.AssigneeId is not null and not 0) ? closedSnapshot.AssigneeId : existingActive.AssigneeId,
                        AssigneeName = (!string.IsNullOrWhiteSpace(closedSnapshot.AssigneeName) && closedSnapshot.AssigneeName.Trim() != "---" && closedSnapshot.AssigneeName.Trim() != "Chưa nhận")
                            ? closedSnapshot.AssigneeName : existingActive.AssigneeName,
                        CreatedAt = existingActive.CreatedAt ?? closedSnapshot.CreatedAt,
                        DepartmentId = closedSnapshot.DepartmentId ?? existingActive.DepartmentId,
                        DepartmentName = !string.IsNullOrWhiteSpace(closedSnapshot.DepartmentName) ? closedSnapshot.DepartmentName : existingActive.DepartmentName,
                        Title = !string.IsNullOrWhiteSpace(existingActive.Title) ? existingActive.Title : closedSnapshot.Title,
                        LatestEmail = TicketChangeDetector.SelectEmail(closedSnapshot.LatestEmail, existingActive.LatestEmail),
                        IsTerminal = true
                    };
                    tickets.Add(closedTicket);
                }
                else
                {
                    tickets.Add(closedSnapshot);
                }
            }
            else if (_missingMonitoredAttempts.TryGetValue(activeCode, out var attempts) && attempts > 3)
            {
                try
                {
                    using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    probeCts.CancelAfter(TimeSpan.FromMilliseconds(1500));
                    var history = await client.GetLatestStatusHistoryAsync(activeCode, TicketStatus.Closed, probeCts.Token);
                    // Chỉ tạo Closed khi có bằng chứng authoritative từ history API
                    if (history?.OccurredAt is not null)
                    {
                        var fallbackClosed = _active[activeCode] with
                        {
                            Status = TicketStatus.Closed,
                            UpdatedAt = history.OccurredAt,
                            ClosedAt = history.OccurredAt,
                            ClosedByName = history.Actor,
                            IsTerminal = true
                        };
                        tickets.Add(fallbackClosed);
                        _closedCache[activeCode] = fallbackClosed;
                    }
                    // Nếu history trả null/không có OccurredAt → KHÔNG tạo synthetic Closed
                    // Ticket sẽ được giữ lại trong _active cho đến khi có xác nhận đóng thật
                }
                catch
                {
                    // API lỗi hoặc timeout → KHÔNG tạo synthetic Closed, giữ ticket trong _active
                    // Sẽ retry ở poll kế tiếp
                }
            }
        }

        // 1. Immediately emit summary for real-time dashboard update
        IReadOnlyList<TicketSnapshot> personal = currentUser is null
            ? []
            : tickets.Where(x => x.AssigneeId == currentUser.UserId).ToList();
        var vietnamToday = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).Date;
        var currentUserName = NormalizeUserName(currentUser?.UserName);
        var personalClosedToday = currentUser is null ? 0 : _closedCache.Values
            .Where(x => x.Status == TicketStatus.Closed && x.ClosedAt is not null &&
                x.ClosedAt.Value.ToOffset(TimeSpan.FromHours(7)).Date == vietnamToday &&
                (x.ClosedByUserId == currentUser.UserId ||
                 x.ClosedByUserId is null && currentUserName is not null &&
                 NormalizeUserName(x.ClosedByName) == currentUserName))
            .Select(x => x.Code)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        SummaryChanged?.Invoke(new DashboardSummary(
            tickets.Count,
            tickets.Count(x => (x.Status is TicketStatus.New or TicketStatus.Assigned) &&
                (x.AssigneeId is null or 0) && (string.IsNullOrWhiteSpace(x.AssigneeName) || x.AssigneeName.Trim() == "---")),
            tickets.Count(x => x.Status == TicketStatus.Assigned),
            tickets.Count(x => x.Status == TicketStatus.InProgress),
            tickets.Count(x => x.Status == TicketStatus.Paused),
            tickets.Count(x => x.Status == TicketStatus.Completed),
            tickets.Count(x => x.Status == TicketStatus.Closed),
            personal.Count(x => !x.Status.IsTerminal(settings.UnprocessedIsTerminal) && x.SlaType == 2),
            personal.Count(x => !x.Status.IsTerminal(settings.UnprocessedIsTerminal) && x.SlaType == 3),
            currentUser,
            personal.Count(x => x.Status == TicketStatus.Assigned),
            personal.Count(x => x.Status == TicketStatus.InProgress),
            personal.Count(x => x.Status == TicketStatus.Paused),
            personalClosedToday));

        // 2. Persist state changes before optional email/SLA/reminder enrichment.
        Dictionary<string, TicketSnapshot> previousSnapshots;
        IReadOnlyList<TicketEvent> criticalEvents;
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            lock (_active)
            {
                previousSnapshots = new Dictionary<string, TicketSnapshot>(_active,
                    StringComparer.OrdinalIgnoreCase);
            }
            var criticalList = detector.DetectCritical(previousSnapshots, tickets, settings).ToList();
            for (var i = 0; i < criticalList.Count; i++)
            {
                var evt = criticalList[i];
                var enriched = await TicketChangeDetector.ResolveLatestEmailWithDeadlineAsync(client, evt.TicketCode,
                    cancellationToken, retryWhenMissing: false, evt.LatestEmail, evt.Snapshot.LatestEmail);
                if (enriched is not null)
                {
                    criticalList[i] = evt with
                    {
                        LatestEmail = enriched,
                        Snapshot = evt.Snapshot with { LatestEmail = enriched }
                    };
                    var matchingTicketIdx = tickets.FindIndex(t => string.Equals(t.Code, evt.TicketCode, StringComparison.OrdinalIgnoreCase));
                    if (matchingTicketIdx >= 0)
                    {
                        tickets[matchingTicketIdx] = tickets[matchingTicketIdx] with { LatestEmail = enriched };
                    }
                }
            }
            criticalEvents = criticalList;
            foreach (var group in criticalEvents.GroupBy(item => item.TicketCode,
                         StringComparer.OrdinalIgnoreCase))
            {
                var snapshot = tickets.Last(item => string.Equals(item.Code, group.Key,
                    StringComparison.OrdinalIgnoreCase));
                var entries = group.Select(item =>
                    (item, FormatIfNotifiable(item,
                        previousSnapshots.GetValueOrDefault(item.TicketCode))))
                    .ToList();
                await store.SaveSnapshotAndEventsAsync(snapshot, entries, cancellationToken);
                UpdateTrackedSnapshot(snapshot);
            }
            var criticalCodes = criticalEvents.Select(item => item.TicketCode)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var snapshot in tickets.Where(item => !criticalCodes.Contains(item.Code)))
            {
                await store.SaveSnapshotAsync(snapshot, cancellationToken);
                UpdateTrackedSnapshot(snapshot);
            }
        }
        finally { _stateLock.Release(); }
        if (criticalEvents.Count > 0) sender.Signal();

        var events = await detector.DetectSupplementalAsync(previousSnapshots, tickets, client, settings, cancellationToken);
        var ihubBase = new Uri(settings.FtmsUrl).GetLeftPart(UriPartial.Authority) + "/ihub";
        foreach (var item in events)
        {
            if (await store.EventExistsAsync(item.EventKey, cancellationToken)) continue;

            // Durable one-shot guard: "TICKET MỚI" chỉ gửi đúng 1 lần cho mỗi ticket
            if (item.EventType == TicketEventType.Created &&
                await store.HasNotificationAsync(item.TicketCode, "Created", "", cancellationToken))
                continue;

            // Durable guard cho reminder: mỗi milestone chỉ gửi 1 lần
            string? ledgerType = null;
            string? ledgerDisc = null;
            if (item.EventType == TicketEventType.UnassignedReminder)
            {
                var minuteMatch = System.Text.RegularExpressions.Regex.Match(item.Reason, @"(\d+) phút");
                ledgerType = "UnassignedReminder";
                ledgerDisc = minuteMatch.Success ? $"unassigned-{int.Parse(minuteMatch.Groups[1].Value) / 5}" : "";
                if (await store.HasNotificationAsync(item.TicketCode, ledgerType, ledgerDisc, cancellationToken))
                    continue;
            }
            else if (item.EventType == TicketEventType.ResponseReminder)
            {
                var minuteMatch = System.Text.RegularExpressions.Regex.Match(item.Reason, @"(\d+) phút");
                ledgerType = "ResponseReminder";
                ledgerDisc = minuteMatch.Success ? $"response-{int.Parse(minuteMatch.Groups[1].Value) / 5}" : "";
                if (await store.HasNotificationAsync(item.TicketCode, ledgerType, ledgerDisc, cancellationToken))
                    continue;
            }

            _active.TryGetValue(item.TicketCode, out var previous);
            var message = TicketNotificationFilter.ShouldNotify(item, previous)
                ? NotificationFormatter.Format(item, ihubBase)
                : null;
            await store.SaveEventAndEnqueueNotificationAsync(item, message, cancellationToken);
            if (!string.IsNullOrWhiteSpace(message)) sender.Signal();

            // Ghi vào ledger sau khi enqueue thành công
            if (item.EventType == TicketEventType.Created)
                await store.RecordNotificationAsync(item.TicketCode, "Created", "", cancellationToken);
            else if (ledgerType is not null && ledgerDisc is not null)
                await store.RecordNotificationAsync(item.TicketCode, ledgerType, ledgerDisc, cancellationToken);
        }

        // 3. Merge supplemental email/reminder state into the tracked snapshots.
        var snapshotsToSave = new List<TicketSnapshot>(tickets.Count);
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var item in tickets)
            {
                TicketSnapshot currentTracked;
                lock (_active)
                {
                    if (!_active.TryGetValue(item.Code, out var act))
                        act = item;
                    currentTracked = act;
                }

                var eventEmail = events.LastOrDefault(x => string.Equals(x.TicketCode, item.Code,
                    StringComparison.OrdinalIgnoreCase))?.LatestEmail;
                var responseEvent = events.LastOrDefault(x =>
                    string.Equals(x.TicketCode, item.Code, StringComparison.OrdinalIgnoreCase) &&
                    x.EventType == TicketEventType.EmailReceived &&
                    currentTracked.Status == TicketStatus.InProgress);
                var keepResponseReminder = currentTracked.Status == TicketStatus.InProgress;
                var snapshot = currentTracked with
                {
                    LatestEmail = eventEmail ??
                        (currentTracked.LatestEmail.IsExcluded() == true ? null : currentTracked.LatestEmail),
                    ResponseReminderEmailId = keepResponseReminder
                        ? responseEvent?.LatestEmail?.Id ?? currentTracked.ResponseReminderEmailId
                        : null,
                    ResponseReminderSince = keepResponseReminder
                        ? responseEvent?.LatestEmail?.SentAt ?? responseEvent?.DetectedAt ??
                          currentTracked.ResponseReminderSince
                        : null,
                    IsTerminal = currentTracked.Status.IsTerminal(settings.UnprocessedIsTerminal)
                };
                snapshotsToSave.Add(snapshot);
                UpdateTrackedSnapshot(snapshot);
            }
            await store.SaveSnapshotsAsync(snapshotsToSave, cancellationToken);
        }
        finally { _stateLock.Release(); }

        await CheckAndRunDailyCleanupAsync(cancellationToken);
        StatusChanged?.Invoke($"Đồng bộ {tickets.Count} ticket, đang theo dõi {_active.Count}");
    }

    private async Task CheckAndRunDailyCleanupAsync(CancellationToken cancellationToken)
    {
        var vietnamNow = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7));
        var currentDay = vietnamNow.Year * 1000 + vietnamNow.DayOfYear;
        if (_lastCleanupDay == currentDay) return;

        try
        {
            await store.CleanupAsync(settings.TerminalRetentionDays, cancellationToken);
            _lastCleanupDay = currentDay;
            _lastCleanupTime = vietnamNow;

            var cutoff = vietnamNow.Date.AddDays(-1);
            var expired = _closedCache.Where(kvp => kvp.Value.ClosedAt is not null &&
                kvp.Value.ClosedAt.Value.ToOffset(TimeSpan.FromHours(7)).Date < cutoff)
                .Select(kvp => kvp.Key).ToList();
            foreach (var key in expired) _closedCache.Remove(key);

            DailyCleanupCompleted?.Invoke();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Lỗi dọn dẹp hàng ngày: {ex.Message}");
        }
    }

    private static string? NormalizeUserName(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) || normalized == "---"
            ? null
            : normalized.ToUpperInvariant();
    }

    private static DashboardSummary UnavailableSummary() => new(0, 0, 0, 0, 0, 0, 0, 0, 0);
}
