using Bazaar.Server.Presence.Abstractions;

namespace Bazaar.Server.Presence;

/// <summary>
/// The in-process presence store. It is the working store until the <c>agent_activity</c> Postgres migration exists (doc 23 work
/// package 3); a restart loses it, and the projector rebuilds the rows from availability and open turns.
/// </summary>
public sealed class InMemoryPresenceStore : IPresenceStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, PresenceRow> _rows = [];

    /// <inheritdoc />
    public ValueTask<bool> UpsertAsync(PresenceRow row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (_gate)
        {
            if (_rows.TryGetValue(row.AgentId, out var held) && held.Revision >= row.Revision)
            {
                return ValueTask.FromResult(false);
            }

            _rows[row.AgentId] = row;
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<PresenceRow?> GetAsync(Guid agentId, CancellationToken ct)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_rows.TryGetValue(agentId, out var row) ? row : null);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<PresenceRow>> GetAllAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyList<PresenceRow>>([.. _rows.Values.OrderBy(r => r.AgentId)]);
        }
    }
}
