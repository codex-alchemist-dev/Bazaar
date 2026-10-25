using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Gateway.Usage;

/// <summary>A thread-safe, in-memory <see cref="IUsageStore"/> for tests and local runs.</summary>
public sealed class InMemoryUsageStore : IUsageStore
{
    private readonly Lock _gate = new();
    private readonly List<GatewayUsageRecord> _records = [];

    /// <summary>Every row appended so far, in append order.</summary>
    public IReadOnlyList<GatewayUsageRecord> Records
    {
        get
        {
            lock (_gate)
            {
                return _records.ToArray();
            }
        }
    }

    public ValueTask AppendAsync(GatewayUsageRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _records.Add(record);
        }

        return ValueTask.CompletedTask;
    }
}
