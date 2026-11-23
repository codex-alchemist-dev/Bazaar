using Bazaar.Server.GeneticMemory.Abstractions;

namespace Bazaar.Server.GeneticMemory;

/// <summary>Seam for the review queue. Staged text lives here, never in the Vault, until a reviewer accepts it.</summary>
public interface IGmtLedger
{
    /// <summary>Adds a candidate.</summary>
    Task AddAsync(GmtCandidate candidate, CancellationToken ct);

    /// <summary>One candidate, or null.</summary>
    Task<GmtCandidate?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>Replaces a candidate with the same id. Returns false when it does not exist.</summary>
    Task<bool> UpdateAsync(GmtCandidate candidate, CancellationToken ct);

    /// <summary>All candidates, oldest first.</summary>
    Task<IReadOnlyList<GmtCandidate>> ListAsync(CancellationToken ct);
}

/// <summary>In-memory ledger for tests and dev runs.</summary>
public sealed class InMemoryGmtLedger : IGmtLedger
{
    private readonly object _gate = new();
    private readonly List<GmtCandidate> _items = [];

    /// <inheritdoc />
    public Task AddAsync(GmtCandidate candidate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_gate)
        {
            _items.Add(candidate);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<GmtCandidate?> GetAsync(Guid id, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_items.Find(c => c.Id == id));
        }
    }

    /// <inheritdoc />
    public Task<bool> UpdateAsync(GmtCandidate candidate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_gate)
        {
            var index = _items.FindIndex(c => c.Id == candidate.Id);
            if (index < 0)
            {
                return Task.FromResult(false);
            }

            _items[index] = candidate;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GmtCandidate>> ListAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<GmtCandidate>>([.. _items]);
        }
    }
}
