using Bazaar.Server.CodingPermissions;
using Bazaar.Server.Security.KillSwitch;

namespace Bazaar.Server.Host.Coding;

/// <summary>
/// Binds the harness's <see cref="IAgentFreeze"/> to Server's <see cref="KillSwitchEngine"/> (doc 15, kill switch). A freeze is
/// an agent stop engaged as the breach actor, which the engine audits and passes to the containment hook. An agent counts as
/// frozen when the engine denies even a read for it: an agent stop, or state not loaded yet (fail closed). A global stop alone
/// does not freeze an agent here, because it leaves reads allowed.
/// </summary>
public sealed class KillSwitchAgentFreeze : IAgentFreeze
{
    private readonly KillSwitchEngine _engine;

    /// <summary>Creates the adapter.</summary>
    public KillSwitchAgentFreeze(KillSwitchEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
    }

    /// <inheritdoc />
    public ValueTask<bool> IsFrozenAsync(string agentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return ValueTask.FromResult(!_engine.Evaluate(agentId, ToolEffect.Read).Allowed);
    }

    /// <inheritdoc />
    public async ValueTask FreezeAsync(string agentId, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await _engine.ReportBreachAsync(agentId, reason, cancellationToken).ConfigureAwait(false);
    }
}
