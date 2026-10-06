using System.Security.Cryptography;
using System.Text;
using FTMS.Domain;

namespace FTMS.Application;

public sealed class TicketChangeDetector
{
    public async Task<IReadOnlyList<TicketEvent>> DetectAsync(IReadOnlyDictionary<string, TicketSnapshot> previous,
        IReadOnlyList<TicketSnapshot> current, IFtmsClient client, AppSettings settings, CancellationToken cancellationToken)
    {
        var events = new List<TicketEvent>();
        foreach (var snapshot in current)
        {
            previous.TryGetValue(snapshot.Code, out var old);
            if (old is null)
            {
                if (snapshot.Status.IsTerminal(settings.UnprocessedIsTerminal) ||
                    snapshot.Status is TicketStatus.InProgress or TicketStatus.Paused) continue;
                var initialEmail = await FetchLatestEmailAsync(client, snapshot.Code, cancellationToken);
                var email = SelectEmail(initialEmail, snapshot.LatestEmail);
                var enriched = snapshot with { LatestEmail = email };
                events.Add(Create(enriched, TicketEventType.Created, null, "Phát hiện ticket mới", email));
                var initialSlaReason = SlaReason(snapshot, settings.SlaThresholds);
                if (initialSlaReason is not null)
                    events.Add(Create(enriched, TicketEventType.SlaThresholdReached, null, initialSlaReason, email,
                        discriminator: snapshot.SlaType == 3 ? "overdue" : $"risk-{snapshot.SlaDeviationMinutes}"));
                continue;
            }

            LatestEmail? fetchedEmail = null;
            var emailFetched = false;
            var emailIncludedInEvent = false;
            async Task<LatestEmail?> LatestEmailAsync(bool retryWhenMissing = true)
            {
                if (emailFetched) return fetchedEmail;
                fetchedEmail = SelectEmail(await FetchLatestEmailAsync(client, snapshot.Code, cancellationToken, retryWhenMissing),
                    snapshot.LatestEmail, old.LatestEmail);
                emailFetched = true;
                return fetchedEmail;
            }

            if (old.Status != snapshot.Status)
            {
                var emailTask = (old.LatestEmail is not null && !old.LatestEmail.IsExcluded() &&
                    !string.IsNullOrWhiteSpace(old.LatestEmail.Body) &&
                    (snapshot.LatestEmail is null || snapshot.LatestEmail.Id == old.LatestEmail.Id))
                    ? Task.FromResult<LatestEmail?>(old.LatestEmail)
                    : LatestEmailAsync();
                var historyTask = LatestStatusHistoryAsync();
                await Task.WhenAll(emailTask, historyTask);
                var email = await emailTask;
                var history = await historyTask;
                var reason = IsNewEmail(old.LatestEmail, email) ? "Có email mới trong luồng ticket" : "Trạng thái ticket đã thay đổi";

                async Task<StatusHistoryEntry?> LatestStatusHistoryAsync()
                {
                    var result = await client.GetLatestStatusHistoryAsync(snapshot.Code, snapshot.Status, cancellationToken);
                    if (result?.OccurredAt is not null) return result;
                    await Task.Delay(200, cancellationToken);
                    return await client.GetLatestStatusHistoryAsync(snapshot.Code, snapshot.Status, cancellationToken) ?? result;
                }
                var isTerminal = snapshot.Status.IsTerminal(settings.UnprocessedIsTerminal);
                var enriched = snapshot with { LatestEmail = email, IsTerminal = isTerminal };
                events.Add(Create(enriched, isTerminal ? TicketEventType.Terminal : TicketEventType.StatusChanged,
                    old.Status, reason, enriched.LatestEmail,
                    discriminator: history?.OccurredAt?.ToString("O") ?? snapshot.UpdatedAt?.ToString("O") ?? string.Empty,
                    changedBy: history?.Actor, changedAt: history?.OccurredAt));
                emailIncludedInEvent = IsNewEmail(old.LatestEmail, email);
            }

            if (!snapshot.Status.IsTerminal(settings.UnprocessedIsTerminal) &&
                (old.AssigneeId != snapshot.AssigneeId || old.DepartmentId != snapshot.DepartmentId))
            {
                var email = (old.LatestEmail is not null && !old.LatestEmail.IsExcluded() &&
                    !string.IsNullOrWhiteSpace(old.LatestEmail.Body) &&
                    (snapshot.LatestEmail is null || snapshot.LatestEmail.Id == old.LatestEmail.Id))
                    ? old.LatestEmail
                    : await LatestEmailAsync(retryWhenMissing: snapshot.LatestEmail is not null);
                var enriched = snapshot with { LatestEmail = email };
                var discriminator = $"{old.AssigneeId}>{snapshot.AssigneeId}|{old.DepartmentId}>{snapshot.DepartmentId}";
                events.Add(Create(enriched, TicketEventType.AssignmentChanged, old.Status, "Người xử lý hoặc phòng ban đã thay đổi",
                    enriched.LatestEmail, discriminator, previousAssigneeName: old.AssigneeName,
                    previousDepartmentName: old.DepartmentName));
                emailIncludedInEvent |= IsNewEmail(old.LatestEmail, email);
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
                        $"Ticket chưa được nhận sau {unassignedMinutes} phút", null,
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
                        $"Ticket đã có phản hồi mới {responseMinutes} phút", null,
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

            if (!snapshot.Status.IsTerminal(settings.UnprocessedIsTerminal) && !emailIncludedInEvent &&
                (old.LatestEmail is null || old.UpdatedAt != snapshot.UpdatedAt))
            {
                var email = await LatestEmailAsync();
                if (IsNewEmail(old.LatestEmail, email) && email is not null)
                {
                    var enriched = snapshot with { LatestEmail = email };
                    events.Add(Create(enriched, TicketEventType.EmailReceived, old.Status,
                        "FTMS có email mới", email, discriminator: email.Id ?? email.SentAt?.ToString("O") ?? string.Empty));
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

    private static bool IsNewEmail(LatestEmail? old, LatestEmail? current)
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

    private static async Task<LatestEmail?> FetchLatestEmailAsync(IFtmsClient client, string code, CancellationToken ct,
        bool retryWhenMissing = true)
    {
        var first = await client.GetLatestEmailAsync(code, ct);
        if (Complete(first) || first is null && !retryWhenMissing) return first;

        await Task.Delay(300, ct);
        var retry = await client.GetLatestEmailAsync(code, ct);
        if (retry is null) return first;
        if (first is null) return retry;
        if (first.SentAt is not null && retry.SentAt is not null && retry.SentAt < first.SentAt)
            return first;
        if (first.SentAt is not null && retry.SentAt is not null && retry.SentAt > first.SentAt)
            return retry;
        if (long.TryParse(first.Id, out var firstId) && long.TryParse(retry.Id, out var retryId))
        {
            if (retryId < firstId) return first;
            if (retryId > firstId) return retry;
        }
        return Completeness(retry) >= Completeness(first) ? retry : first;

        static bool Complete(LatestEmail? email) => email is not null &&
            !string.IsNullOrWhiteSpace(email.From) && email.SentAt is not null &&
            !string.IsNullOrWhiteSpace(email.Body) && !IsTruncatedPreview(email.Body);
        static int Completeness(LatestEmail email) =>
            (string.IsNullOrWhiteSpace(email.From) ? 0 : 1) +
            (email.SentAt is null ? 0 : 1) +
            (string.IsNullOrWhiteSpace(email.Subject) ? 0 : 1) +
            (string.IsNullOrWhiteSpace(email.Body) ? 0 : 1) +
            (!string.IsNullOrWhiteSpace(email.Body) && !IsTruncatedPreview(email.Body) ? 1 : 0);
    }

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

    private static LatestEmail? SelectEmail(params LatestEmail?[] candidates)
    {
        var valid = candidates.Where(email => email is not null && !email.IsExcluded()).Select(email => email!).ToList();
        if (valid.Count == 0) return null;

        var fetched = valid.FirstOrDefault(e => ReferenceEquals(e, candidates.FirstOrDefault()));
        if (fetched is not null && !string.IsNullOrWhiteSpace(fetched.Body) && !IsTruncatedPreview(fetched.Body))
            return fetched;

        var newest = valid.OrderByDescending(e => e.SentAt).First();
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
        string? previousAssigneeName = null, string? previousDepartmentName = null)
    {
        var raw = $"{snapshot.Code}|{type}|{previous}|{snapshot.Status}|{email?.Id}|{discriminator}";
        return new TicketEvent
        {
            EventKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))),
            TicketCode = snapshot.Code, EventType = type, PreviousStatus = previous,
            CurrentStatus = snapshot.Status, DetectedAt = DateTimeOffset.Now, Reason = reason,
            ChangedBy = changedBy, ChangedAt = changedAt, PreviousAssigneeName = previousAssigneeName,
            PreviousDepartmentName = previousDepartmentName, LatestEmail = email, Snapshot = snapshot
        };
    }
}
