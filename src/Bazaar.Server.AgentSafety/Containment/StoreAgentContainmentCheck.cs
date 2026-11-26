using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.AgentSafety.Containment;

/// <summary>
/// The <see cref="IAgentContainmentCheck"/> over the durable containment store: an agent in the store may not have a VM started.
/// Ids are compared as GUIDs, so the text form the store holds does not matter. An unreadable store throws, which keeps the VM down.
/// </summary>
public sealed class StoreAgentContainmentCheck(IContainmentStateStore store) : IAgentContainmentCheck
{
    /// <inheritdoc />
    public async Task<bool> IsContainedAsync(Guid agentId, CancellationToken ct)
    {
        var contained = await store.LoadAsync(ct).ConfigureAwait(false);
        return contained.Any(c => Guid.TryParse(c.AgentId, out var id) && id == agentId);
    }
}
