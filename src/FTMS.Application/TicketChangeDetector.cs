using System.Security.Cryptography;
using System.Text;
using FTMS.Domain;

namespace FTMS.Application;

public sealed class TicketChangeDetector
{
    public IReadOnlyList<TicketEvent> DetectCritical(
        IReadOnlyDictionary<string, TicketSnapshot> previous,
        IReadOnlyList<TicketSnapshot> current,
        AppSettings settings)
    {
        var events = new List<TicketEvent>();
        foreach (var snapshot in current)
        {
            previous.TryGetValue(snapshot.Code, out var old);
            var preservedEmail = SelectEmail(snapshot.LatestEmail, old?.LatestEmail);
            var effectiveSnapshot = snapshot with
            {
                LatestEmail = preservedEmail,
                AssigneeId = (snapshot.AssigneeId is not null and not 0) ? snapshot.AssigneeId : old?.AssigneeId,
                AssigneeName = (!string.IsNullOrWhiteSpace(snapshot.AssigneeName) && snapshot.AssigneeName.Trim() != "---" && snapshot.AssigneeName.Trim() != "Chưa nhận")
                    ? snapshot.AssigneeName : old?.AssigneeName,
                CreatedAt = old?.CreatedAt ?? snapshot.CreatedAt,
                DepartmentId = snapshot.DepartmentId ?? old?.DepartmentId,
                DepartmentName = !string.IsNullOrWhiteSpace(snapshot.DepartmentName) ? snapshot.DepartmentName : old?.DepartmentName,
                Title = !string.IsNullOrWhiteSpace(old?.Title) ? old.Title : snapshot.Title
            };

            if (old is null)
            {
                if (snapshot.Status.IsTerminal(settings.UnprocessedIsTerminal) ||
                    snapshot.Status is TicketStatus.InProgress or TicketStatus.Paused) continue;
                var email = effectiveSnapshot.LatestEmail.IsExcluded() ? null : effectiveSnapshot.LatestEmail;
                events.Add(Create(effectiveSnapshot with { LatestEmail = email }, TicketEventType.Created, null,
                    "Phát hiện ticket mới", email));
                continue;
            }

            var statusChanged = old.Status != snapshot.Status;
            var assignmentChanged = old.AssigneeId != snapshot.AssigneeId ||
                old.DepartmentId != snapshot.DepartmentId;
            var changedAt = snapshot.UpdatedAt ?? DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7));

            if (statusChanged && snapshot.Status.IsTerminal(settings.UnprocessedIsTerminal))
            {
                events.Add(Create(effectiveSnapshot with { IsTerminal = true },
                    TicketEventType.Terminal, old.Status, "Trạng thái ticket đã thay đổi", effectiveSnapshot.LatestEmail,
                    changedAt.ToString("O"), snapshot.UpdatedBy, changedAt));
                continue;
            }

            if (assignmentChanged && !snapshot.Status.IsTerminal(settings.UnprocessedIsTerminal))
            {
                var discriminator = $"{old.AssigneeId}>{snapshot.AssigneeId}|" +
                    $"{old.DepartmentId}>{snapshot.DepartmentId}|{old.Status}>{snapshot.Status}|{changedAt:O}";
                events.Add(Create(effectiveSnapshot, TicketEventType.AssignmentChanged,
                    old.Status, "Người xử lý hoặc phòng ban đã thay đổi", effectiveSnapshot.LatestEmail, discriminator,
                    snapshot.UpdatedBy, changedAt, old.AssigneeName, old.DepartmentName));
                continue;
            }

            if (statusChanged)
            {
                events.Add(Create(effectiveSnapshot, TicketEventType.StatusChanged,
                    old.Status, "Trạng thái ticket đã thay đổi", effectiveSnapshot.LatestEmail, changedAt.ToString("O"),
                    snapshot.UpdatedBy, changedAt));
            }
        }
        return events;
    }

    public async Task<IReadOnlyList<TicketEvent>> DetectAsync(
        IReadOnlyDictionary<string, TicketSnapshot> previous,
        IReadOnlyList<TicketSnapshot> current, IFtmsClient client, AppSettings settings,
        CancellationToken cancellationToken)
    {
        var criticalEvents = DetectCritical(previous, current, settings);
        var result = new List<TicketEvent>(criticalEvents);
        var enrichedCurrent = current.ToList();
        for (var i = 0; i < result.Count; i++)
        {
            var evt = result[i];
            if (evt.EventType is not (TicketEventType.Created or TicketEventType.StatusChanged or TicketEventType.Terminal or TicketEventType.AssignmentChanged))
                continue;

            var enriched = await ResolveLatestEmailWithDeadlineAsync(client, evt.TicketCode, cancellationToken,
                retryWhenMissing: false, evt.LatestEmail, evt.Snapshot.LatestEmail);
            if (enriched is null) continue;

            var enrichedSnapshot = evt.Snapshot with { LatestEmail = enriched };
            result[i] = evt with
            {
                LatestEmail = enriched,
                Snapshot = enrichedSnapshot
            };
            var currentIndex = enrichedCurrent.FindIndex(item =>
                string.Equals(item.Code, evt.TicketCode, StringComparison.OrdinalIgnoreCase));
            if (currentIndex >= 0)
                enrichedCurrent[currentIndex] = enrichedCurrent[currentIndex] with { LatestEmail = enriched };
        }
        var supplementalEvents = await DetectSupplementalAsync(previous, enrichedCurrent, client, settings, cancellationToken);
        result.AddRange(supplementalEvents);
        return result;
    }

    public async Task<IReadOnlyList<TicketEvent>> DetectSupplementalAsync(
        IReadOnlyDictionary<string, TicketSnapshot> previous,
        IReadOnlyList<TicketSnapshot> current, IFtmsClient client, AppSettings settings,
        CancellationToken cancellationToken)
    {
        var events = new List<TicketEvent>();
        foreach (var snapshot in current)
        {
            previous.TryGetValue(snapshot.Code, out var old);
            if (old is null)
            {
                if (snapshot.Status.IsTerminal(settings.UnprocessedIsTerminal) ||
                    snapshot.Status is TicketStatus.InProgress or TicketStatus.Paused) continue;
                var initialSlaReason = SlaReason(snapshot, settings.SlaThresholds);
                if (initialSlaReason is not null)
                {
                    var email = snapshot.LatestEmail.IsExcluded() ? null : snapshot.LatestEmail;
                    events.Add(Create(snapshot with { LatestEmail = email },
                        TicketEventType.SlaThresholdReached, null, initialSlaReason, email,
                        discriminator: snapshot.SlaType == 3 ? "overdue" : $"risk-{snapshot.SlaDeviationMinutes}"));
                }
                continue;
            }

            LatestEmail? fetchedEmail = null;
            var emailFetched = false;
            var isStateTransition = old.Status != snapshot.Status || old.AssigneeId != snapshot.AssigneeId;
            var emailIncludedInEvent = isStateTransition && IsNewEmail(old.LatestEmail, snapshot.LatestEmail);
            var hasEmailHint = snapshot.LatestEmail is not null || old.LatestEmail is not null;
            async Task<LatestEmail?> LatestEmailAsync()
            {
                if (emailFetched) return fetchedEmail;
                fetchedEmail = await ResolveLatestEmailWithDeadlineAsync(client, snapshot.Code,
                    cancellationToken, retryWhenMissing: hasEmailHint, snapshot.LatestEmail, old.LatestEmail);
                emailFetched = true;
                return fetchedEmail;
            }


            var isWaitingForReceiver = snapshot.Status is TicketStatus.New or TicketStatus.Assigned &&
                snapshot.AssigneeId is null or 0 &&
                (string.IsNullOrWhiteSpace(snapshot.AssigneeName) || snapshot.AssigneeName.Trim() == "---");
            if (isWaitingForReceiver && snapshot.CreatedAt is not null)
            {
                var unassignedMinutes = Math.Max(0, (int)(DateTimeOffset.Now - snapshot.CreatedAt.Value).TotalMinutes);
                var reminderBucket = unassignedMinutes / 5;
                if (reminderBucket >= 1 && reminderBucket <= 6) // Chỉ nhắc tối đa 6 mốc: 5, 10, 15, 20, 25, 30 phút
                {
                    var email = await LatestEmailAsync();
                    var enriched = snapshot with { LatestEmail = email };
                    events.Add(Create(enriched, TicketEventType.UnassignedReminder, old.Status,
                        $"Ticket chưa được nhận sau {unassignedMinutes} phút", email,
                        discriminator: $"unassigned-{reminderBucket}"));
                    emailIncludedInEvent |= IsNewEmail(old.LatestEmail, email);
                }
            }

            if (snapshot.Status == TicketStatus.InProgress &&
                !string.IsNullOrWhiteSpace(old.ResponseReminderEmailId) && old.ResponseReminderSince is not null)
            {
                var responseMinutes = Math.Max(0, (int)(DateTimeOffset.Now - old.ResponseReminderSince.Value).TotalMinutes);
                var responseBucket = responseMinutes / 5;
                if (responseBucket >= 1 && responseBucket <= 6) // Chỉ nhắc tối đa 6 mốc: 5, 10, 15, 20, 25, 30 phút
                {
                    var email = await LatestEmailAsync();
                    var enriched = snapshot with
                    {
                        LatestEmail = email,
                        ResponseReminderEmailId = old.ResponseReminderEmailId,
                        ResponseReminderSince = old.ResponseReminderSince
                    };
                    events.Add(Create(enriched, TicketEventType.ResponseReminder, old.Status,
                        $"Ticket đã có phản hồi mới {responseMinutes} phút", email,
                        discriminator: $"response-{old.ResponseReminderEmailId}-{responseBucket}"));
                    emailIncludedInEvent |= IsNewEmail(old.LatestEmail, email);
                }
            }

            var crossedThreshold = snapshot.SlaType == 2 && snapshot.SlaDeviationMinutes is > 0
                ? settings.SlaThresholds.Where(threshold => snapshot.SlaDeviationMinutes <= threshold &&
                    (old.SlaType != 2 || old.SlaDeviationMinutes is null || old.SlaDeviationMinutes > threshold))
                    .OrderBy(threshold => threshold)
                    .FirstOrDefault()
                : 0;
            if (crossedThreshold > 0)
            {
                var email = SelectEmail(await FetchLatestEmailAsync(client, snapshot.Code, cancellationToken),
                    snapshot.LatestEmail, old.LatestEmail);
                var enriched = snapshot with { LatestEmail = email };
                events.Add(Create(enriched, TicketEventType.SlaThresholdReached, old.Status,
                    $"Sắp vi phạm SLA: còn {snapshot.SlaDeviationMinutes} phút", enriched.LatestEmail,
                    discriminator: $"risk-{crossedThreshold}"));
            }

            if (snapshot.SlaType == 3 && old.SlaType != 3 &&
                !snapshot.Status.IsTerminal(settings.UnprocessedIsTerminal))
            {
                var email = SelectEmail(await FetchLatestEmailAsync(client, snapshot.Code, cancellationToken),
                    snapshot.LatestEmail, old.LatestEmail);
                var enriched = snapshot with { LatestEmail = email };
                events.Add(Create(enriched, TicketEventType.SlaThresholdReached, old.Status, "Đã vi phạm SLA",
                    enriched.LatestEmail, "overdue"));
            }

            if (!snapshot.Status.IsTerminal(settings.UnprocessedIsTerminal) && !emailIncludedInEvent)
            {
                var listHasNewEmail = IsNewEmail(old.LatestEmail, snapshot.LatestEmail);
                if (listHasNewEmail || old.LatestEmail is null || old.UpdatedAt != snapshot.UpdatedAt)
                {
                    var email = await LatestEmailAsync();
                    if (IsNewEmail(old.LatestEmail, email) && email is not null)
                    {
                        var enriched = snapshot with { LatestEmail = email };
                        events.Add(Create(enriched, TicketEventType.EmailReceived, old.Status,
                            "FTMS có email mới", email,
                            discriminator: email.Id ?? email.SentAt?.ToString("O") ?? string.Empty));
                    }
                }
            }

        }
        return events;
    }

    private static string? SlaReason(TicketSnapshot snapshot, IReadOnlyCollection<int> thresholds)
    {
        if (snapshot.SlaType == 3) return "Đã vi phạm SLA";
        if (snapshot.SlaType != 2 || snapshot.SlaDeviationMinutes is not > 0) return null;
        var highestThreshold = thresholds.Count == 0 ? 0 : thresholds.Max();
        return snapshot.SlaDeviationMinutes <= highestThreshold
            ? $"Sắp vi phạm SLA: còn {snapshot.SlaDeviationMinutes} phút"
            : null;
    }

    internal static bool IsNewEmail(LatestEmail? old, LatestEmail? current)
    {
        if (current is null || current.IsExcluded()) return false;
        if (old is null) return true;
        if (current.SentAt is not null && old.SentAt is not null)
        {
            if (current.SentAt > old.SentAt) return true;
            if (current.SentAt < old.SentAt) return false;
        }
        return !string.IsNullOrWhiteSpace(current.Id) && current.Id != old.Id &&
            (!long.TryParse(current.Id, out var currentId) || !long.TryParse(old.Id, out var oldId) || currentId > oldId);
    }

    internal static async Task<LatestEmail?> ResolveLatestEmailWithDeadlineAsync(IFtmsClient client,
        string code, CancellationToken cancellationToken, bool retryWhenMissing,
        params LatestEmail?[] cachedCandidates)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(800));
        try
        {
            return await ResolveLatestEmailAsync(client, code, timeout.Token, retryWhenMissing,
                true, cachedCandidates);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SelectEmail(cachedCandidates);
        }
        catch
        {
            return SelectEmail(cachedCandidates);
        }
    }

    internal static async Task<LatestEmail?> ResolveLatestEmailAsync(IFtmsClient client, string code,
        CancellationToken cancellationToken, bool retryWhenMissing = true, bool retryWhenUnchanged = false,
        params LatestEmail?[] cachedCandidates)
    {
        var cachedLatest = SelectEmail(cachedCandidates);
        var fetched = await FetchLatestEmailAsync(client, code, cancellationToken, retryWhenMissing,
            retryWhenUnchanged ? cachedLatest : null);
        return SelectEmail(fetched, cachedLatest);
    }

    private static async Task<LatestEmail?> FetchLatestEmailAsync(IFtmsClient client, string code, CancellationToken ct,
        bool retryWhenMissing = true, LatestEmail? retryWhenSameAs = null)
    {
        var first = await client.GetLatestEmailAsync(code, ct);
        var shouldRetry = !IsCompleteEmail(first) && (first is not null || retryWhenMissing) ||
            retryWhenSameAs is not null && !IsNewEmail(retryWhenSameAs, first);
        if (!shouldRetry) return first;

        await Task.Delay(300, ct);
        var retry = await client.GetLatestEmailAsync(code, ct);
        return SelectEmail(first, retry);
    }

    internal static bool IsCompleteEmail(LatestEmail? email) => email is not null &&
        !email.IsExcluded() &&
        !string.IsNullOrWhiteSpace(email.From) && email.SentAt is not null &&
        !string.IsNullOrWhiteSpace(email.Body) && !IsTruncatedPreview(email.Body);

    private static bool IsTruncatedPreview(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        var trimmed = body.TrimEnd();
        if (trimmed.EndsWith("...") || trimmed.EndsWith("…") ||
            trimmed.EndsWith("&nb...", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith("&nb…", StringComparison.OrdinalIgnoreCase))
            return true;
        var singleLine = System.Text.RegularExpressions.Regex.Replace(trimmed, @"\s+", " ");
        if (singleLine.Length <= 80 &&
            System.Text.RegularExpressions.Regex.IsMatch(singleLine, @"^(?:dear|hi|hello|chao|chào|xin chao|xin chào)\s+\S+(?:\s+\S+){0,5}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return true;
        return false;
    }

    public static LatestEmail? SelectEmail(params LatestEmail?[] candidates)
    {
        var valid = candidates.Where(email => email is not null && !email.IsExcluded()).Select(email => email!).ToList();
        if (valid.Count == 0) return null;

        var newest = valid
            .OrderByDescending(e => e.SentAt)
            .ThenByDescending(e => long.TryParse(e.Id, out var id) ? id : 0)
            .First();
        var fullMatchForNewest = valid
            .Where(e => !string.IsNullOrWhiteSpace(e.Body) && !IsTruncatedPreview(e.Body) &&
                (!string.IsNullOrWhiteSpace(e.Id) && string.Equals(e.Id, newest.Id, StringComparison.OrdinalIgnoreCase) ||
                 e.SentAt is not null && newest.SentAt is not null && Math.Abs((e.SentAt.Value - newest.SentAt.Value).TotalSeconds) < 60))
            .OrderByDescending(e => e.Body?.Length ?? 0)
            .FirstOrDefault();

        if (fullMatchForNewest is not null) return fullMatchForNewest;

        return valid
            .OrderByDescending(email => email.SentAt)
            .ThenByDescending(email => !string.IsNullOrWhiteSpace(email.Body) && !IsTruncatedPreview(email.Body) ? 1 : 0)
            .ThenByDescending(email => email.Body?.Length ?? 0)
            .ThenByDescending(email => long.TryParse(email.Id, out var id) ? id : 0)
            .FirstOrDefault();
    }

    private static TicketEvent Create(TicketSnapshot snapshot, TicketEventType type, TicketStatus? previous,
        string reason, LatestEmail? email, string discriminator = "", string? changedBy = null, DateTimeOffset? changedAt = null,
        string? previousAssigneeName = null, string? previousDepartmentName = null, string? note = null)
    {
        var emailIdentity = type == TicketEventType.EmailReceived
            ? email?.Id ?? email?.SentAt?.ToString("O")
            : null;
        var raw = type is TicketEventType.StatusChanged or TicketEventType.Terminal
            ? $"{snapshot.Code}|{type}|{previous}|{snapshot.Status}"
            : type == TicketEventType.EmailReceived
                ? $"{snapshot.Code}|{type}|{emailIdentity}"
                : $"{snapshot.Code}|{type}|{previous}|{snapshot.Status}|{emailIdentity}|{discriminator}";
        return new TicketEvent
        {
            EventKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))),
            TicketCode = snapshot.Code, EventType = type, PreviousStatus = previous,
            CurrentStatus = snapshot.Status, DetectedAt = DateTimeOffset.Now, Reason = reason,
            ChangedBy = changedBy, ChangedAt = changedAt, PreviousAssigneeName = previousAssigneeName,
            PreviousDepartmentName = previousDepartmentName, LatestEmail = email, Note = note, Snapshot = snapshot
        };
    }
}
