namespace Bazaar.Server.Setup.FirstRun;

/// <summary>The group chat the seed made or found.</summary>
/// <param name="GcId">The GC id.</param>
/// <param name="Created">True when this call created it; false when it already existed.</param>
public sealed record SeededGc(Guid GcId, bool Created);

/// <summary>The agent the seed made or found.</summary>
/// <param name="AgentId">The agent id.</param>
/// <param name="Created">True when this call created it; false when it already existed.</param>
public sealed record SeededAgent(Guid AgentId, bool Created);

/// <summary>Who the owner is. The owner step (doc 45 section 1.3, step 6) runs first, so a seed with no owner is a setup-order failure.</summary>
public interface IFirstRunOwner
{
    /// <summary>The owner's principal id, or null when no owner exists yet.</summary>
    Task<Guid?> GetOwnerPrincipalIdAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates the seeded rows (doc 45 section 1.15). Every method is idempotent: the seed rows carry a fixed marker, so a second call
/// finds them and changes nothing.
/// </summary>
public interface IFirstRunStore
{
    /// <summary>Creates the first GC of type <c>general</c> with the owner as a member, or returns the one that exists.</summary>
    Task<SeededGc> EnsureGeneralGcAsync(string title, Guid ownerPrincipalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the default agent as <c>provisioned</c> (not active), with exactly these whitelisted routes, and adds it to
    /// <paramref name="gcId"/> when one is given. An existing seeded agent is returned unchanged.
    /// </summary>
    Task<SeededAgent> EnsureDefaultAgentAsync(string name, string purpose, Guid? gcId, IReadOnlyList<string> whitelist, CancellationToken cancellationToken = default);

    /// <summary>Moves the agent from provisioned to active.</summary>
    Task ActivateAgentAsync(Guid agentId, CancellationToken cancellationToken = default);

    /// <summary>Rollback: removes a seeded agent and its memberships.</summary>
    Task RemoveAgentAsync(Guid agentId, CancellationToken cancellationToken = default);

    /// <summary>Rollback: archives a seeded GC. The GC log is append-only, so it is never deleted (doc 01).</summary>
    Task ArchiveGcAsync(Guid gcId, CancellationToken cancellationToken = default);
}

/// <summary>The owner's Constitution acceptance (doc 24: no activation without an accepted Constitution commit).</summary>
public interface IConstitutionAcceptance
{
    /// <summary>Records the owner's acceptance of the current Constitution commit. Returns false when there is no commit to accept.</summary>
    Task<bool> RecordOwnerAcceptanceAsync(Guid ownerPrincipalId, CancellationToken cancellationToken = default);
}

/// <summary>The routes of the default agent: Claude models from the gateway's sources, as provider-qualified names (doc 05, doc 91).</summary>
public interface IDefaultAgentRoutes
{
    /// <summary>
    /// The whitelist for the default agent. <paramref name="requestedSources"/> narrows it to those source ids; empty means every
    /// Claude source the gateway has. Empty result when the gateway has none. Never a source the gateway does not list.
    /// </summary>
    Task<IReadOnlyList<string>> GetWhitelistAsync(IReadOnlyList<string> requestedSources, CancellationToken cancellationToken = default);
}

/// <summary>Writes the one audit row of the seed. The row holds ids and flags, never a name or a secret.</summary>
public interface IFirstRunAudit
{
    Task WriteSeededAsync(Guid? gcId, Guid? agentId, bool constitutionAccepted, string operatorIdentity, CancellationToken cancellationToken = default);
}
