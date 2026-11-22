namespace Bazaar.GitHub.Issues;

/// <summary>
/// The narrow seam to GC creation for issue GCs (doc 21, issue GCs). Same two idempotent calls as the PR port, kept separate so an
/// operator can bind a different GC type or creator for issues, and so registrations never collide.
/// </summary>
public interface IGitHubIssueGcFactory
{
    /// <summary>Creates the issue GC with its members and metadata when <paramref name="gcId"/> is new, and does nothing otherwise. Returns true when this call created it.</summary>
    Task<bool> EnsureAsync(Guid gcId, string title, Guid createdBy, IReadOnlyList<Guid> members, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken);

    /// <summary>Adds an agent to an existing issue GC. A current member changes nothing. Returns true when this call added it.</summary>
    Task<bool> AddAgentAsync(Guid gcId, Guid agentId, string displayName, CancellationToken cancellationToken);
}

/// <summary>Operator options for issue GCs, bound from <c>Bazaar:GitHub:IssueGc</c> (doc 21, issue GCs).</summary>
public sealed class GitHubIssueGcOptions
{
    /// <summary>
    /// The owner principal, which creates each issue GC and is a member so the owner reads it. When null the PR option
    /// (<c>Bazaar:GitHub:PrGc:OwnerPrincipalId</c>) is used, and when that is null too the bridge principal creates it (OPEN: owner read access then).
    /// </summary>
    public Guid? OwnerPrincipalId { get; init; }

    /// <summary>The GC type key. The <c>github</c> component registers <c>issue</c> (OPEN: doc 21 names no type). A different key must be registered in <c>gc_type</c> by the operator.</summary>
    public string GcTypeKey { get; init; } = "issue";

    /// <summary>At most this many agents are members at creation. Later ones are added by their own events.</summary>
    public int MaxAgentMembersAtCreate { get; init; } = 7;
}
