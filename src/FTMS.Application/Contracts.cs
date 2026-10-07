using FTMS.Domain;

namespace FTMS.Application;

public interface IFtmsClient
{
    Task<bool> IsAuthenticatedAsync(CancellationToken cancellationToken);
    Task<CurrentUserIdentity?> GetCurrentUserAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<TicketSnapshot>> GetTicketsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<TicketSnapshot>> GetTicketsAsync(bool includeHistory, CancellationToken cancellationToken) =>
        GetTicketsAsync(cancellationToken);
    Task<IReadOnlyList<TicketSnapshot>> GetClosedTicketsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TicketSnapshot>>([]);
    Task<TicketClaimResult> ClaimTicketAsync(string ticketCode, long expectedUserId, CancellationToken cancellationToken);
    Task<LatestEmail?> GetLatestEmailAsync(string ticketCode, CancellationToken cancellationToken);
    Task<StatusHistoryEntry?> GetLatestStatusHistoryAsync(string ticketCode, TicketStatus status, CancellationToken cancellationToken);
    Task BeginLoginRecoveryAsync(CancellationToken cancellationToken);
}

public interface ITicketStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, TicketSnapshot>> LoadActiveSnapshotsAsync(CancellationToken cancellationToken);
    Task SaveSnapshotAsync(TicketSnapshot snapshot, CancellationToken cancellationToken);
    Task SaveSnapshotsAsync(IReadOnlyList<TicketSnapshot> snapshots, CancellationToken cancellationToken);
    Task SaveEventAsync(TicketEvent ticketEvent, CancellationToken cancellationToken);
    Task<bool> EventExistsAsync(string eventKey, CancellationToken cancellationToken);
    Task EnqueueNotificationAsync(TicketEvent ticketEvent, string message, CancellationToken cancellationToken);
    Task SaveEventAndEnqueueNotificationAsync(TicketEvent ticketEvent, string? message, CancellationToken cancellationToken);
    Task SaveSnapshotAndEventsAsync(TicketSnapshot snapshot,
        IReadOnlyList<(TicketEvent Event, string? Message)> events, CancellationToken cancellationToken);
    Task MarkTerminalAsync(string code, DateTimeOffset terminalAt, CancellationToken cancellationToken);
    Task<bool> HasNotificationAsync(string ticketCode, string notificationType, string discriminator, CancellationToken cancellationToken);
    Task RecordNotificationAsync(string ticketCode, string notificationType, string discriminator, CancellationToken cancellationToken);
    Task CleanupAsync(int retentionDays, CancellationToken cancellationToken);
}

public interface INotificationSender
{
    void Signal();
    Task RunAsync(CancellationToken cancellationToken);
}
