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
