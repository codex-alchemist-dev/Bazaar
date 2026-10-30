namespace Bazaar.Server.Memory.Access;

/// <summary>
/// The identity a Vault read is made under. <see cref="AgentId"/> is set for <see cref="VaultPrincipalKind.Agent"/>
/// and <see cref="VaultPrincipalKind.Cover"/> (the cover agent's own id); <see cref="PrimaryAgentId"/> is set for
/// Cover only; <see cref="MemberId"/> is set for <see cref="VaultPrincipalKind.MemberHuman"/> so project membership
/// can be checked. Use the factory methods; they validate every identifier.
/// </summary>
public sealed record VaultPrincipal
{
    private VaultPrincipal(VaultPrincipalKind kind, string? agentId, string? primaryAgentId, string? memberId)
    {
        Kind = kind;
        AgentId = agentId;
        PrimaryAgentId = primaryAgentId;
        MemberId = memberId;
    }

    /// <summary>The principal kind.</summary>
    public VaultPrincipalKind Kind { get; }

    /// <summary>The agent id for Agent and Cover principals (the cover's own id for Cover); null otherwise.</summary>
    public string? AgentId { get; }

    /// <summary>The primary agent a Cover principal covers for; null otherwise.</summary>
    public string? PrimaryAgentId { get; }

    /// <summary>The human member id for MemberHuman principals; null otherwise.</summary>
    public string? MemberId { get; }

    /// <summary>The owner.</summary>
    public static VaultPrincipal Owner() => new(VaultPrincipalKind.Owner, null, null, null);

    /// <summary>The consolidation (maintenance) job.</summary>
    public static VaultPrincipal Maintenance() => new(VaultPrincipalKind.Maintenance, null, null, null);

    /// <summary>An agent reading with its own identity.</summary>
    /// <exception cref="ArgumentException">The id is not a valid single path segment.</exception>
    public static VaultPrincipal Agent(string agentId) =>
        new(VaultPrincipalKind.Agent, RequireIdentifier(agentId, nameof(agentId)), null, null);

    /// <summary>The cover agent, reading for <paramref name="primaryAgentId"/>.</summary>
    /// <exception cref="ArgumentException">Either id is not a valid single path segment.</exception>
    public static VaultPrincipal Cover(string coverAgentId, string primaryAgentId) =>
        new(
            VaultPrincipalKind.Cover,
            RequireIdentifier(coverAgentId, nameof(coverAgentId)),
            RequireIdentifier(primaryAgentId, nameof(primaryAgentId)),
            null);

    /// <summary>A co-member human who is not the owner.</summary>
    /// <exception cref="ArgumentException">The id is not a valid single path segment.</exception>
    public static VaultPrincipal MemberHuman(string memberId) =>
        new(VaultPrincipalKind.MemberHuman, null, null, RequireIdentifier(memberId, nameof(memberId)));

    private static string RequireIdentifier(string identifier, string paramName)
    {
        if (!VaultPath.IsValidIdentifier(identifier))
        {
            throw new ArgumentException("Identifier must be a single valid path segment.", paramName);
        }

        return identifier;
    }
}
