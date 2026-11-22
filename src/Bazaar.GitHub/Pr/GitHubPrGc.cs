namespace Bazaar.GitHub.Pr;

/// <summary>
/// The narrow seam to GC creation (doc 21 work package 6; doc 01 GC types). The PR service creates and extends PR GCs through
/// this port only, so it binds to no GC module internals. Both calls are idempotent. No implementation ships yet: the public
/// seam is <c>IGcProvisioner</c> (core #1099: <c>CreateAsync</c> with a type key and metadata, <c>AddMemberAsync</c>), which is on
/// main but not on this branch, and the branch's <c>GcCreateService</c> has neither the <c>pr</c> type key, metadata nor member adds.
/// The host registers an adapter over <c>IGcProvisioner</c> (create with the creator, then add each other member) when it lands.
/// Until a factory is registered the PR service stays off and the sink is unchanged.
/// </summary>
public interface IGitHubPrGcFactory
{
    /// <summary>
    /// Creates the PR GC with its members and metadata when <paramref name="gcId"/> is new, and does nothing otherwise.
    /// The id is derived from the PR node id, so a retry or a concurrent caller finds the same GC.
    /// </summary>
    /// <returns>True when this call created the GC.</returns>
    Task<bool> EnsureAsync(Guid gcId, string title, Guid createdBy, IReadOnlyList<Guid> members, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken);

    /// <summary>Adds an agent to an existing PR GC. A current member changes nothing. Returns true when this call added it.</summary>
    Task<bool> AddAgentAsync(Guid gcId, Guid agentId, string displayName, CancellationToken cancellationToken);
}

/// <summary>Operator options for PR GCs, bound from <c>Bazaar:GitHub:PrGc</c> (doc 40, GitHub PR GC creation).</summary>
public sealed class GitHubPrGcOptions
{
    /// <summary>
    /// The owner principal. When set it creates the GC and is a member, which is how the owner reads every PR GC (doc 01: the owner
    /// reads all). When null the bridge principal creates it and the owner is not a member (OPEN: how the owner gets read access then).
    /// </summary>
    public Guid? OwnerPrincipalId { get; init; }

    /// <summary>The GC type key. Doc 21 registers <c>pr</c>.</summary>
    public string GcTypeKey { get; init; } = "pr";

    /// <summary>At most this many agents are members at creation (the create cap is 9, less the owner and the bridge). Later ones are added by their own events.</summary>
    public int MaxAgentMembersAtCreate { get; init; } = 7;
}
