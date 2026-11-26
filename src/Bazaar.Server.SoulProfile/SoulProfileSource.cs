namespace Bazaar.Server.SoulProfile;

/// <summary>
/// Builds a profile from the four things that know about it: access (who may read), identity (the agent row), the binding (which
/// Soul) and the version chain. Every miss gives the same null (doc 07, read-only profile route), so the route cannot reveal whether
/// an agent exists. Versions are returned oldest first.
/// </summary>
public sealed class SoulProfileSource(
    ISoulProfileAccess access,
    IAgentIdentityReader identities,
    ISoulBindingReader bindings,
    ISoulVersionReader versions) : ISoulProfileSource
{
    public async Task<AgentProfileInfo?> ReadAsync(string personId, Guid agentId, CancellationToken cancellationToken = default)
    {
        if (!await access.CanReadAsync(personId, agentId, cancellationToken).ConfigureAwait(false)) return null;
        if (await identities.ReadAsync(agentId, cancellationToken).ConfigureAwait(false) is not { } identity) return null;
        if (await bindings.ResolveSoulAsync(agentId, cancellationToken).ConfigureAwait(false) is not { } soulId) return null;
        var chain = await versions.ListAsync(soulId, cancellationToken).ConfigureAwait(false);
        return new AgentProfileInfo(soulId, identity.Name, identity.Role, identity.ModelBadge, [.. chain.OrderBy(v => v.Version)]);
    }
}
