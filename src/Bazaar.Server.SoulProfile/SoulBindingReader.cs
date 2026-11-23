using Bazaar.Souls.Abstractions;

namespace Bazaar.Server.SoulProfile;

/// <summary>Resolves an agent's Soul through the Souls module's own read side (<see cref="ISoulBindings"/>). Unbound, empty and retired agents give null.</summary>
public sealed class SoulBindingReader(ISoulBindings bindings) : ISoulBindingReader
{
    public async Task<Guid?> ResolveSoulAsync(Guid agentId, CancellationToken cancellationToken = default) =>
        await bindings.ResolveAsync(agentId, cancellationToken).ConfigureAwait(false) is { } soul ? soul.SoulId : null;
}
