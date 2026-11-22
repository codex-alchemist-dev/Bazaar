namespace Bazaar.Server.Security.Approvals;

/// <summary>
/// A request for an owner Tier 2 approval that does not come from a live tool dispatch (doc 15, "Peer approval of account
/// creation", enqueue entry point). It can only ask: nothing here approves anything.
/// </summary>
/// <param name="AgentId">The agent the approval is about.</param>
/// <param name="Tool">
/// The tool name the approval records, in the form of existing tool names (lowercase dotted, for example
/// <c>account.request</c>).
/// </param>
/// <param name="ActionKey">
/// A stable key for this exact action. The same key never creates a second approval. The caller picks one key per
/// request, for example the request id.
/// </param>
/// <param name="Summary">
/// What the owner reads on the card, in plain words. The caller must keep secrets out of it; the enqueuer only caps its
/// length and refuses control characters.
/// </param>
public sealed record OwnerApprovalEnqueueRequest(string AgentId, string Tool, string ActionKey, string Summary)
{
    /// <summary>Longest accepted agent id, tool name and action key, in characters.</summary>
    public const int MaxIdentifierLength = 128;

    /// <summary>Longest accepted summary, in characters. Matches the card excerpt cap.</summary>
    public const int MaxSummaryLength = 1000;
}

/// <summary>
/// Lets a module ask for an owner Tier 2 approval without a tool dispatch (doc 15). The approval goes through the same card
/// and owner-signed decision path as any Tier 2 approval. An implementation that cannot store the request throws, so the
/// caller refuses; it never reports success without a stored request.
/// </summary>
public interface IOwnerApprovalEnqueuer
{
    /// <summary>
    /// Stores a pending Tier 2 approval for the request and returns its id. Idempotent on the action key: a repeat, also a
    /// concurrent one, returns the id of the existing approval whatever its status, and creates no second row.
    /// </summary>
    /// <exception cref="ArgumentException">An input is blank, too long, malformed or holds a control character.</exception>
    /// <exception cref="InvalidOperationException">The action key already belongs to a different request.</exception>
    Task<Guid> EnqueueAsync(OwnerApprovalEnqueueRequest request, CancellationToken ct = default);
}
