using Bazaar.Server.Community.Profile;

namespace Bazaar.Server.Community.Tools;

/// <summary>Who an agent is on one platform, as the server knows it: the sponsor who answers for its posts and the disclosed profile.</summary>
/// <param name="SponsorId">The human sponsor of the agent.</param>
/// <param name="Profile">The agent's bot profile on the platform; its <see cref="BotProfile.AgentId"/> is the agent's id in "D" form.</param>
public sealed record CommunityAgentIdentity(string SponsorId, BotProfile Profile);

/// <summary>
/// Resolves the calling agent's sponsor and bot profile (doc 36, "Bot profile conventions"). The identity never comes from the model's
/// arguments: the propose tool asks this seam with the caller the dispatcher supplied. No default is registered, so until the host binds
/// one the tool refuses with <c>no_profile</c> rather than guessing a sponsor.
/// </summary>
public interface ICommunityAgentIdentitySource
{
    /// <summary>Returns the identity, or null when the agent has no profile on that platform.</summary>
    Task<CommunityAgentIdentity?> FindAsync(Guid agentId, string platform, CancellationToken ct = default);
}

/// <summary>
/// Reads the taint sources of a calling session for the action log (doc 15, "Untrusted input"). The default reports none; the host binds
/// the session taint store when it wires the community component.
/// </summary>
public interface ICommunityTaintSourceReader
{
    /// <summary>Returns a JSON array of taint sources for the session, "[]" when none.</summary>
    Task<string> ReadJsonAsync(Guid sessionId, CancellationToken ct = default);
}
