using Bazaar.Server.OutcomeLedger.Abstractions;

namespace Bazaar.Server.OutcomeLedger;

/// <summary>In-memory ledger, used when no database is registered and in tests.</summary>
public sealed class InMemoryOutcomeLedgerStore : IOutcomeLedgerStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Guid> _byKey = new(StringComparer.Ordinal);
    private readonly List<(Guid Id, OutcomeRecord Record, DateTimeOffset RecordedAt)> _rows = [];

    /// <summary>A snapshot of what was recorded, oldest first.</summary>
    public IReadOnlyList<(Guid Id, OutcomeRecord Record, DateTimeOffset RecordedAt)> Rows
    {
        get
        {
            lock (_gate)
            {
                return [.. _rows];
            }
        }
    }

    public Task<Guid?> AppendAsync(OutcomeRecord resolved, DateTimeOffset recordedAt, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_byKey.ContainsKey(resolved.DedupeKey))
            {
                return Task.FromResult<Guid?>(null);
            }

            var id = Guid.CreateVersion7();
            _byKey[resolved.DedupeKey] = id;
            _rows.Add((id, resolved, recordedAt));
            return Task.FromResult<Guid?>(id);
        }
    }
}
