using System.Text.RegularExpressions;

namespace FTMS.Domain;

public enum TicketStatus { New = 0, Assigned = 1, InProgress = 2, Completed = 3, Paused = 4, Closed = 5, Cancelled = 7, Unprocessed = 8 }

public static class TicketStatusExtensions
{
    public static bool IsTerminal(this TicketStatus value, bool unprocessedIsTerminal = true) =>
        value is TicketStatus.Closed or TicketStatus.Cancelled || unprocessedIsTerminal && value is TicketStatus.Unprocessed;

    public static string DisplayName(this TicketStatus value) => value switch
    {
        TicketStatus.New => "T\u1ea1o m\u1edbi", TicketStatus.Assigned => "Ph\u00e2n c\u00f4ng",
        TicketStatus.InProgress => "Đang thực hiện", TicketStatus.Completed => "Ho\u00e0n th\u00e0nh",
        TicketStatus.Paused => "T\u1ea1m ng\u01b0ng", TicketStatus.Closed => "\u0110\u00e3 \u0111\u00f3ng",
        TicketStatus.Cancelled => "\u0110\u00e3 h\u1ee7y", TicketStatus.Unprocessed => "Kh\u00f4ng x\u1eed l\u00fd", _ => value.ToString()
    };
}

public sealed record LatestEmail(string? Id, DateTimeOffset? SentAt, string? From, string? Subject, string? Body);

public static class LatestEmailExtensions
{
    public static bool IsIgnoredSender(this LatestEmail? email)
    {
        var from = email?.From?.Trim() ?? string.Empty;
        return new[] { "fti.sd02@fpt.com", "ihub.akabot2@fpt.com", "ihub.akabot2", "ducvm19@fpt.com" }
            .Any(sender => from.Contains(sender, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsAkabot(this LatestEmail? email) => email is not null &&
        (string.Equals(email.From?.Trim(), "ihub.akabot2@fpt.com", StringComparison.OrdinalIgnoreCase) ||
         email.Body?.Contains("ihub.akabot2@fpt.com", StringComparison.OrdinalIgnoreCase) == true);

    public static bool IsAutomatedAcknowledgement(this LatestEmail? email)
    {
        if (email?.Body is null) return false;
        var body = email.Body;
        var quoted = Regex.Match(body, @"<hr\b|-----Original Message-----|From:\s*.{0,200}?\b(?:Sent|Date):",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (quoted.Success) body = body[..quoted.Index];
        var standardReceipt = body.Contains("Thông tin yêu cầu hỗ trợ", StringComparison.OrdinalIgnoreCase) &&
            body.Contains("đã được tiếp nhận", StringComparison.OrdinalIgnoreCase) &&
            body.Contains("chuyển đến bộ phận", StringComparison.OrdinalIgnoreCase);
        var technicalReceipt = body.Contains("Thông tin hỗ trợ", StringComparison.OrdinalIgnoreCase) &&
            body.Contains("đã được tiếp nhận", StringComparison.OrdinalIgnoreCase) &&
            body.Contains("Kỹ thuật sẽ kiểm tra và phản hồi", StringComparison.OrdinalIgnoreCase);
        var assignmentReceipt = body.Contains("Kỹ thuật FPT nhận thông tin YCHT", StringComparison.OrdinalIgnoreCase) &&
            body.Contains("phân công nhân sự xử lý theo RQ", StringComparison.OrdinalIgnoreCase);
        return standardReceipt || technicalReceipt || assignmentReceipt;
    }

    public static bool IsExcluded(this LatestEmail? email) => email.IsIgnoredSender() || email.IsAutomatedAcknowledgement();
}

public sealed record StatusHistoryEntry(TicketStatus Status, DateTimeOffset? OccurredAt, string? Actor, string? Note = null);

public enum TicketClaimStatus
{
    Claimed,
    AlreadyOwnedByCurrentUser,
    OwnedByAnotherUser,
    NotFound,
    NotClaimable,
    AuthenticationRequired,
    RetryableFailure
}

public sealed record TicketClaimResult(TicketClaimStatus Status, string Message)
{
    public bool IsSuccess => Status is TicketClaimStatus.Claimed or TicketClaimStatus.AlreadyOwnedByCurrentUser;
    public bool IsRetryable => Status is TicketClaimStatus.AuthenticationRequired or TicketClaimStatus.RetryableFailure;
}

public sealed record TicketSnapshot
{
    public required string Code { get; init; }
    public TicketStatus Status { get; init; }
    public string? Title { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public string? UpdatedBy { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
    public long? ClosedByUserId { get; init; }
    public string? ClosedByName { get; init; }
    public long? AssigneeId { get; init; }
    public string? AssigneeName { get; init; }
    public long? DepartmentId { get; init; }
    public string? DepartmentName { get; init; }
    public int? SlaDeviationMinutes { get; init; }
    public int? SlaType { get; init; }
    public LatestEmail? LatestEmail { get; init; }
    public string? ResponseReminderEmailId { get; init; }
    public DateTimeOffset? ResponseReminderSince { get; init; }
    public bool IsTerminal { get; init; }
}

public enum TicketEventType { Created, StatusChanged, AssignmentChanged, SlaThresholdReached, Terminal, EmailReceived, UnassignedReminder, ResponseReminder }

public sealed record TicketEvent
{
    public required string EventKey { get; init; }
    public required string TicketCode { get; init; }
    public required TicketEventType EventType { get; init; }
    public TicketStatus? PreviousStatus { get; init; }
    public TicketStatus CurrentStatus { get; init; }
    public required DateTimeOffset DetectedAt { get; init; }
    public required string Reason { get; init; }
    public string? ChangedBy { get; init; }
    public DateTimeOffset? ChangedAt { get; init; }
    public string? PreviousAssigneeName { get; init; }
    public string? PreviousDepartmentName { get; init; }
    public LatestEmail? LatestEmail { get; init; }
    public string? Note { get; init; }
    public required TicketSnapshot Snapshot { get; init; }
}

public sealed record AppSettings
{
    public string FtmsUrl { get; init; } = "https://ftms.fpt.net/ihub/list?tab=2";
    public int PollIntervalSeconds { get; init; } = 30;
    public int IdleDelaySeconds { get; init; } = 10;
    public bool AutoRefreshEnabled { get; init; } = true;
    public string TelegramBotToken { get; init; } = string.Empty;
    public string TelegramChatId { get; init; } = string.Empty;
    public int[] SlaThresholds { get; init; } = [5, 3, 1];
    public int TerminalRetentionDays { get; init; } = 1;
    public bool UnprocessedIsTerminal { get; init; } = true;
    public int HistoryIntervalSeconds { get; init; } = 60;
}

public sealed record CurrentUserIdentity(long UserId, string? UserName, long? DepartmentId, string? DepartmentName);

public sealed record DashboardSummary(int Total, int New, int Assigned, int InProgress, int Paused, int Completed, int Closed,
    int SlaRisk, int SlaViolated, CurrentUserIdentity? CurrentUser = null, int PersonalAssigned = 0,
    int PersonalInProgress = 0, int PersonalPaused = 0, int PersonalClosedToday = 0)
{
    public int PersonalWorkloadTotal => PersonalAssigned + PersonalInProgress + PersonalPaused;
    public bool HasCurrentUser => CurrentUser is not null;
}

public sealed record TelegramCommandResponse(string Text, object[][]? InlineKeyboard = null);

public enum TicketActionStatus
{
    Success,
    AlreadyInTargetState,
    OwnedByAnotherUser,
    NotFound,
    NotAllowed,
    AuthenticationRequired,
    RetryableFailure
}

public sealed record TicketActionResult(TicketActionStatus Status, string Message)
{
    public bool IsSuccess => Status is TicketActionStatus.Success or TicketActionStatus.AlreadyInTargetState;
    public bool IsRetryable => Status is TicketActionStatus.AuthenticationRequired or TicketActionStatus.RetryableFailure;
}
