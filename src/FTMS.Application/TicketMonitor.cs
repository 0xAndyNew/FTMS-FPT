using FTMS.Domain;

namespace FTMS.Application;

public sealed class TicketMonitor(IFtmsClient client, ITicketStore store, INotificationSender sender,
    TicketChangeDetector detector, AppSettings settings)
{
    private readonly Dictionary<string, TicketSnapshot> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TicketSnapshot> _closedCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _missingMonitoredAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private DateTimeOffset _lastHistoryFetch = DateTimeOffset.MinValue;
    private bool _forceHistoryNext = true;
    private DateTimeOffset _lastCleanupTime = DateTimeOffset.MinValue;
    private int _lastCleanupDay = -1;
    public event Action<string>? StatusChanged;
    public event Action<DashboardSummary>? SummaryChanged;
    public event Action? DailyCleanupCompleted;
    public DateTimeOffset LastCleanupTime => _lastCleanupTime;
    public int LastCleanupDay => _lastCleanupDay;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await store.InitializeAsync(cancellationToken);
        foreach (var item in await store.LoadActiveSnapshotsAsync(cancellationToken)) _active[item.Key] = item.Value;
        await CheckAndRunDailyCleanupAsync(cancellationToken);
    }

    public async Task RunAsync(Func<bool> userIsActive, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!userIsActive() && !await client.IsAuthenticatedAsync(cancellationToken))
                {
                    SummaryChanged?.Invoke(UnavailableSummary());
                    await client.BeginLoginRecoveryAsync(cancellationToken);
                }
                else if (!userIsActive())
                {
                    await SyncNowAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (UnauthorizedAccessException)
            {
                SummaryChanged?.Invoke(UnavailableSummary());
                if (!userIsActive()) await client.BeginLoginRecoveryAsync(cancellationToken);
            }
            catch (Exception ex) { StatusChanged?.Invoke($"Loi: {ex.Message}"); }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, settings.PollIntervalSeconds)), cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    public Task SyncNowAsync(CancellationToken cancellationToken) =>
        SyncNowAsync(forceHistory: false, cancellationToken);

    public async Task SyncNowAsync(bool forceHistory, CancellationToken cancellationToken)
    {
        if (forceHistory) _forceHistoryNext = true;
        if (!await _syncLock.WaitAsync(0, cancellationToken)) return;
        try
        {
            var includeHistory = _forceHistoryNext;
            _forceHistoryNext = false;
            await PollOnceAsync(includeHistory, cancellationToken);
            await sender.SendPendingAsync(cancellationToken);
        }
        finally { _syncLock.Release(); }
    }

    private async Task PollOnceAsync(bool forceHistoryUpfront, CancellationToken cancellationToken)
    {
        var historyDue = forceHistoryUpfront ||
            _lastHistoryFetch == DateTimeOffset.MinValue ||
            (DateTimeOffset.UtcNow - _lastHistoryFetch) >= TimeSpan.FromSeconds(Math.Max(10, settings.HistoryIntervalSeconds));

        var apiTickets = await client.GetTicketsAsync(includeHistory: historyDue, cancellationToken);
        var currentUser = await client.GetCurrentUserAsync(cancellationToken);

        if (historyDue)
        {
            _lastHistoryFetch = DateTimeOffset.UtcNow;
            foreach (var item in apiTickets.Where(x => x.Status == TicketStatus.Closed))
            {
                _closedCache[item.Code] = item;
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
                    _closedCache[item.Code] = item;
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
            if (activeTicket is not null && !activeTicket.Status.IsTerminal(settings.UnprocessedIsTerminal))
                _closedCache.Remove(code);
        }

        // Closed history is only relevant when it closes a ticket already being monitored.
        var tickets = apiTickets.Where(item => !item.Status.IsTerminal(settings.UnprocessedIsTerminal) ||
            _active.ContainsKey(item.Code)).ToList();

        foreach (var activeCode in _active.Keys)
        {
            if (tickets.Any(x => string.Equals(x.Code, activeCode, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (_closedCache.TryGetValue(activeCode, out var closedSnapshot))
            {
                tickets.Add(closedSnapshot);
            }
            else if (_missingMonitoredAttempts.TryGetValue(activeCode, out var attempts) && attempts > 3)
            {
                try
                {
                    var history = await client.GetLatestStatusHistoryAsync(activeCode, TicketStatus.Closed, cancellationToken);
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
                    // API lỗi → KHÔNG tạo synthetic Closed, giữ ticket trong _active
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

        // 2. Detect changes & queue notifications (email scraping etc.)
        var events = await detector.DetectAsync(_active, tickets, client, settings, cancellationToken);
        var ihubBase = new Uri(settings.FtmsUrl).GetLeftPart(UriPartial.Authority) + "/ihub";
        foreach (var item in events)
        {
            if (await store.EventExistsAsync(item.EventKey, cancellationToken)) continue;

            // Durable one-shot guard: "TICKET MỚI" chỉ gửi đúng 1 lần cho mỗi ticket
            if (item.EventType == TicketEventType.Created &&
                await store.HasNotificationAsync(item.TicketCode, "Created", "", cancellationToken))
                continue;

            // Durable guard cho reminder: mỗi milestone chỉ gửi 1 lần
            if (item.EventType == TicketEventType.UnassignedReminder)
            {
                var disc = item.Reason; // discriminator đã có trong EventKey
                var bucket = disc; // sẽ dùng ticket code + type + discriminator từ event
                // Lấy discriminator từ EventKey hash - dùng lại logic: event key chứa discriminator
                // Thay vào đó dùng trực tiếp reason chứa số phút
                var minuteMatch = System.Text.RegularExpressions.Regex.Match(item.Reason, @"(\d+) phút");
                var reminderDisc = minuteMatch.Success ? $"unassigned-{int.Parse(minuteMatch.Groups[1].Value) / 5}" : "";
                if (await store.HasNotificationAsync(item.TicketCode, "UnassignedReminder", reminderDisc, cancellationToken))
                    continue;
            }

            _active.TryGetValue(item.TicketCode, out var previous);
            var message = TicketNotificationFilter.ShouldNotify(item, previous)
                ? NotificationFormatter.Format(item, ihubBase)
                : null;
            await store.SaveEventAndEnqueueNotificationAsync(item, message, cancellationToken);

            // Ghi vào ledger sau khi enqueue thành công
            if (item.EventType == TicketEventType.Created)
                await store.RecordNotificationAsync(item.TicketCode, "Created", "", cancellationToken);
            else if (item.EventType == TicketEventType.UnassignedReminder)
            {
                var minuteMatch = System.Text.RegularExpressions.Regex.Match(item.Reason, @"(\d+) phút");
                var reminderDisc = minuteMatch.Success ? $"unassigned-{int.Parse(minuteMatch.Groups[1].Value) / 5}" : "";
                await store.RecordNotificationAsync(item.TicketCode, "UnassignedReminder", reminderDisc, cancellationToken);
            }
        }

        // 3. Batch save snapshots to SQLite in a single transaction
        var snapshotsToSave = new List<TicketSnapshot>(tickets.Count);
        foreach (var item in tickets)
        {
            var eventEmail = events.LastOrDefault(x => string.Equals(x.TicketCode, item.Code, StringComparison.OrdinalIgnoreCase))?.LatestEmail;
            _active.TryGetValue(item.Code, out var previous);
            var responseEvent = events.LastOrDefault(x =>
                string.Equals(x.TicketCode, item.Code, StringComparison.OrdinalIgnoreCase) &&
                x.EventType == TicketEventType.StatusChanged && x.CurrentStatus == TicketStatus.InProgress &&
                x.Reason.Contains("email mới", StringComparison.OrdinalIgnoreCase));
            var keepResponseReminder = item.Status == TicketStatus.InProgress;
            var snapshot = item with
            {
                LatestEmail = eventEmail ?? (previous?.LatestEmail.IsExcluded() == true ? null : previous?.LatestEmail),
                ResponseReminderEmailId = keepResponseReminder
                    ? responseEvent?.LatestEmail?.Id ?? previous?.ResponseReminderEmailId
                    : null,
                ResponseReminderSince = keepResponseReminder
                    ? responseEvent?.LatestEmail?.SentAt ?? responseEvent?.DetectedAt ?? previous?.ResponseReminderSince
                    : null,
                IsTerminal = item.Status.IsTerminal(settings.UnprocessedIsTerminal)
            };
            snapshotsToSave.Add(snapshot);
            if (snapshot.IsTerminal)
            {
                _active.Remove(snapshot.Code);
                _missingMonitoredAttempts.Remove(snapshot.Code);
                if (snapshot.Status == TicketStatus.Closed) _closedCache[snapshot.Code] = snapshot;
            }
            else _active[snapshot.Code] = snapshot;
        }
        await store.SaveSnapshotsAsync(snapshotsToSave, cancellationToken);

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
