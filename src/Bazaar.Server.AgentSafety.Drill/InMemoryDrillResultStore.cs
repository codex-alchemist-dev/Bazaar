namespace Bazaar.Server.AgentSafety.Drill;

/// <summary>In-memory <see cref="IDrillResultStore"/>. Runs last only as long as the process; the default when no durable store is registered.</summary>
public sealed class InMemoryDrillResultStore : IDrillResultStore
{
    private readonly Lock _gate = new();
    private readonly List<DrillResult> _results = [];

    /// <inheritdoc />
    public Task AddAsync(DrillResult result, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _results.Add(result);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<DrillResult?> LatestAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_results.OrderByDescending(r => r.StartedAt).FirstOrDefault());
        }
    }
}
