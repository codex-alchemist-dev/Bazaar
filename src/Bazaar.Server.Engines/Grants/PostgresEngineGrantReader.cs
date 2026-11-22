using Bazaar.Contracts.Engines;
using Bazaar.Server.EngineDispatch;

namespace Bazaar.Server.Engines.Grants;

/// <summary>
/// The Postgres adapter for the dispatcher's <see cref="IEngineGrantReader"/> seam (doc 20, Data and interfaces), over
/// <see cref="EngineGrantStore.CheckAsync"/>. References only <c>Bazaar.Server.EngineDispatch.Abstractions</c>, never the dispatcher.
/// </summary>
/// <remarks>
/// Grant id assumption (PROPOSED): <c>engine_grants</c> is keyed by (agent, adapter, tool) and has no id column, so a grant id
/// string cannot be resolved to a row. Until doc 15's <c>approval</c> ids back the grants (see the TODO on
/// <see cref="EngineGrantStore"/>), the id is an opaque reference that carries no authority: the row for the agent, adapter,
/// tool and risk class is the grant. A blank id fails closed. Exceptions from the store propagate; the dispatcher denies on them.
/// </remarks>
public sealed class PostgresEngineGrantReader(EngineGrantStore store) : IEngineGrantReader
{
    /// <inheritdoc />
    public async ValueTask<bool> CoversAsync(Guid agentId, string adapterId, string toolName, RiskClass riskClass, string grantId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(grantId) || string.IsNullOrWhiteSpace(adapterId) || string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        return await store.CheckAsync(agentId, adapterId, toolName, riskClass, ct).ConfigureAwait(false);
    }
}
