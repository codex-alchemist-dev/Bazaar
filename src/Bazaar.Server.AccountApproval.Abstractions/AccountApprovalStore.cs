namespace Bazaar.Server.AccountApproval.Abstractions;

/// <summary>One stored request with its recorded votes (oldest first) and its current decision.</summary>
/// <param name="Decision">The latest decision. <see cref="Pending"/> until a final one is recorded; a final decision is never replaced.</param>
public sealed record AccountApprovalRecord(
    AccountApprovalRequest Request,
    IReadOnlyList<PeerVote> Votes,
    AccountApprovalDecision Decision);

/// <summary>What the store did with a vote it was asked to append.</summary>
public enum StoredVoteOutcome
{
    /// <summary>The vote and the new decision are stored.</summary>
    Stored,

    /// <summary>The same approver already has a vote of the same kind on this request. Nothing was stored.</summary>
    DuplicateVote,

    /// <summary>The request already has a final decision. Nothing was stored.</summary>
    AlreadyDecided,

    /// <summary>No such request. Nothing was stored.</summary>
    UnknownRequest,
}

/// <summary>
/// Where <c>AccountApprovalService</c> keeps its requests, votes and decisions (doc 15, doc 59 section 7). Rows are append-only: a
/// vote is never changed or removed, and a decision is appended, never overwritten. Each write that adds a vote and a decision is
/// atomic, and it applies only while the request is still pending, so a vote is never counted twice and a decided request is never
/// decided again, even when two callers race. Any exception means nothing was stored; callers must treat it as "not approved".
/// </summary>
public interface IAccountApprovalStore
{
    /// <summary>The request with its votes and current decision, or null when the id is unknown.</summary>
    Task<AccountApprovalRecord?> GetAsync(string requestId, CancellationToken ct = default);

    /// <summary>Stores a new request and its first decision together. Returns false, storing nothing, when the id already exists.</summary>
    Task<bool> TryCreateAsync(AccountApprovalRequest request, AccountApprovalDecision decision, CancellationToken ct = default);

    /// <summary>
    /// Appends a vote and the decision it led to, together, while the request is pending. The pair is unique per approver and vote kind.
    /// </summary>
    Task<StoredVoteOutcome> TryAppendVoteAsync(PeerVote vote, AccountApprovalDecision decision, CancellationToken ct = default);

    /// <summary>Appends a decision for a request that is still pending. Returns false, storing nothing, when it is already decided or unknown.</summary>
    Task<bool> TryDecideAsync(string requestId, AccountApprovalDecision decision, CancellationToken ct = default);
}
