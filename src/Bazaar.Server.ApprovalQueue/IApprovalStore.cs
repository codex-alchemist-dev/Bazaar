namespace Bazaar.Server.ApprovalQueue;

/// <summary>
/// Storage seam under <see cref="ApprovalQueueService"/>. Statuses it returns are the stored ones (never
/// <see cref="ApprovalStatus.Expired"/>); the service applies expiry on read. Each transition is one atomic statement.
/// </summary>
public interface IApprovalStore
{
    Task InsertAsync(ApprovalItem item, CancellationToken ct = default);

    Task<ApprovalItem?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Pending rows with <c>ExpiresAt &gt; now</c>, oldest first.</summary>
    Task<IReadOnlyList<ApprovalItem>> ListPendingAsync(string? kind, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Moves a pending, unexpired row to approved or denied. False when the row is not in that state.</summary>
    Task<bool> TryDecideAsync(Guid id, Guid approverId, bool approve, string? reason, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Moves an approved row to consumed. True for exactly one caller.</summary>
    Task<bool> TryConsumeAsync(Guid id, DateTimeOffset now, CancellationToken ct = default);
}
