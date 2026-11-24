namespace Bazaar.Server.Memory.Access;

/// <summary>
/// Who proposed each staged company candidate (docs/design/08-memory-system.md, 6.5). <see cref="VaultPathAccess"/> asks here, because
/// <c>company/candidates/**</c> is readable by the owner, maintenance and the proposing agent only. Without a source every agent is
/// denied all candidates (fail closed).
/// </summary>
public interface ICompanyCandidateProposers
{
    /// <summary>The proposing agent's id for the candidate at <paramref name="candidatePath"/>; null for an unknown candidate or an owner proposal.</summary>
    string? ProposerOf(VaultPath candidatePath);
}

/// <summary>
/// Whether an agent is quarantined (docs/design/15 containment). A quarantined agent gets no reads of <c>company/**</c>
/// (docs/design/08-memory-system.md, 6.5).
/// </summary>
public interface ICompanyAgentQuarantine
{
    /// <summary>True when the agent is quarantined. A source that cannot tell should answer true.</summary>
    bool IsQuarantined(string agentId);
}
