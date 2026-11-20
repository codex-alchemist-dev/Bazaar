namespace Bazaar.Server.ApprovalQueue;

/// <summary>
/// Where an approval request stands (docs/design/51-web-admin-module.md, approvals queue). <see cref="Expired"/> is computed on
/// read: a pending item past its expiry reads as expired, and nothing is written for it.
/// </summary>
public enum ApprovalStatus
{
    Pending,
    Approved,
    Denied,
    Expired,
    Consumed,
}

/// <summary>A new request. <paramref name="Tier"/> is 1 (any approver) or 2 (a human approver). <paramref name="PayloadRef"/> names the thing to approve; the queue never holds its contents.</summary>
public sealed record NewApproval(string Kind, int Tier, string Subject, Guid RequesterPrincipalId, string? PayloadRef, TimeSpan Ttl);

/// <summary>One queue row as read. Never carries payload contents.</summary>
public sealed record ApprovalItem(
    Guid Id,
    string Kind,
    int Tier,
    string Subject,
    Guid RequesterPrincipalId,
    string? PayloadRef,
    ApprovalStatus Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt,
    Guid? DecidedBy,
    DateTimeOffset? DecidedAt,
    string? Reason);

/// <summary>Why a request or decision was refused.</summary>
public enum ApprovalRefusal
{
    InvalidRequest,
    NotFound,
    NotPending,
    SelfApproval,
    NotPermitted,
    HumanRequired,
}

/// <summary>Thrown when a request or decision breaks a rule. Nothing changed.</summary>
public sealed class ApprovalRefusedException(ApprovalRefusal refusal, string message) : InvalidOperationException(message)
{
    public ApprovalRefusal Refusal { get; } = refusal;
}

/// <summary>
/// The shared approvals queue: the admin panel lists it and the mail thread's gateway adapter files requests into it. A decision
/// is made once; a consumer then spends an approved item exactly once.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Name fixed by the cross-thread contract; it is not a System.Collections.Queue.")]
public interface IApprovalQueue
{
    /// <summary>Files a pending request and returns its id. The ttl is clamped to 1 minute at least and 30 days at most.</summary>
    /// <exception cref="ApprovalRefusedException">Tier is not 1 or 2, or kind or subject is blank.</exception>
    Task<Guid> RequestAsync(NewApproval request, CancellationToken ct = default);

    Task<ApprovalItem?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Pending items that have not expired, oldest first, optionally for one kind.</summary>
    Task<IReadOnlyList<ApprovalItem>> ListPendingAsync(string? kind, CancellationToken ct = default);

    /// <summary>
    /// Approves or denies a pending item. The requester may not decide their own request, the approver must hold the approval
    /// capability, and a tier 2 item needs a human approver.
    /// </summary>
    /// <exception cref="ApprovalRefusedException">A rule was broken; the item is unchanged.</exception>
    Task<ApprovalItem> DecideAsync(Guid id, Guid approverPrincipalId, bool approve, string? reason, CancellationToken ct = default);

    /// <summary>Spends an approved item. True exactly once per approval; false for anything else (pending, denied, spent, unknown).</summary>
    Task<bool> TryConsumeAsync(Guid id, CancellationToken ct = default);
}
