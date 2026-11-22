namespace Bazaar.Server.SoulProfile;

/// <summary>One entry of a Soul's version chain as the profile route shows it: metadata only, never the charter text.</summary>
public sealed record SoulVersionInfo(
    int Version,
    int? ParentVersion,
    string CharterSha256,
    string PreferencesJson,
    string ContentSha256,
    string Author,
    string Reason,
    string? DecisionRef,
    DateTimeOffset CreatedAt);

/// <summary>An agent's profile card and version chain (doc 07, read-only profile route).</summary>
public sealed record AgentProfileInfo(Guid SoulId, string Name, string Role, string ModelBadge, IReadOnlyList<SoulVersionInfo> Versions);

/// <summary>Turns the request's <c>Authorization</c> header into the person it names, or null when the token is missing or not valid.</summary>
public interface ISoulProfileCallerResolver
{
    /// <summary>The person id, or null when the caller is not a signed-in person.</summary>
    Task<string?> ResolveAsync(string? authorizationHeader, CancellationToken cancellationToken = default);
}

/// <summary>Reads a profile for a person. Returns null when the agent does not exist, is unbound, or the person may not read it.</summary>
public interface ISoulProfileSource
{
    /// <summary>The profile, or null (one answer for every reason, so the route reveals nothing).</summary>
    Task<AgentProfileInfo?> ReadAsync(string personId, Guid agentId, CancellationToken cancellationToken = default);
}

/// <summary>The identity fields of an agent row that the profile card shows (doc 06 and doc 07: identity is the agent row's).</summary>
/// <param name="Name">Display name.</param>
/// <param name="Role">Role title, such as "Developer".</param>
/// <param name="ModelBadge">Short model label, such as "Haiku 5.5". Empty when none is set.</param>
public sealed record AgentIdentity(string Name, string Role, string ModelBadge);

/// <summary>Reads an agent's identity fields. The Agents module supplies it. Returns null for an unknown agent.</summary>
public interface IAgentIdentityReader
{
    Task<AgentIdentity?> ReadAsync(Guid agentId, CancellationToken cancellationToken = default);
}

/// <summary>Resolves the Soul an agent runs under right now, or null when the agent is unbound, retired or has no versions (doc 07, agent binding).</summary>
public interface ISoulBindingReader
{
    Task<Guid?> ResolveSoulAsync(Guid agentId, CancellationToken cancellationToken = default);
}

/// <summary>Reads a Soul's version chain, in any order. The Souls module supplies it.</summary>
public interface ISoulVersionReader
{
    Task<IReadOnlyList<SoulVersionInfo>> ListAsync(Guid soulId, CancellationToken cancellationToken = default);
}

/// <summary>Decides whether a person may read an agent's profile. The owner reads all (decided inputs); the rule is the server's, not the route's.</summary>
public interface ISoulProfileAccess
{
    Task<bool> CanReadAsync(string personId, Guid agentId, CancellationToken cancellationToken = default);
}
