namespace Bazaar.Server.Security.Approvals;

/// <summary>
/// Process-local <see cref="IApprovalStore"/> for tests and single-process use. The Postgres store (migration 0011)
/// is the durable one. Thread-safe.
/// </summary>
public sealed class InMemoryApprovalStore : IApprovalStore, IPendingApprovalLister
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ApprovalRequest> _rows = [];

    /// <summary>Number of stored rows.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _rows.Count;
            }
        }
    }

    /// <inheritdoc />
    public Task InsertAsync(ApprovalRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (!_rows.TryAdd(request.Id, request))
            {
                throw new InvalidOperationException($"Approval {request.Id} already exists.");
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ApprovalRequest?> GetAsync(Guid id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.GetValueOrDefault(id));
        }
    }

    /// <inheritdoc />
    public Task<bool> TryReplaceAsync(Guid id, ApprovalStatus expected, ApprovalRequest updated, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(updated);
        lock (_gate)
        {
            if (!_rows.TryGetValue(id, out var current) || current.Status != expected || updated.Id != id)
            {
                return Task.FromResult(false);
            }

            _rows[id] = updated;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ApprovalRequest>> ListPendingAsync(DateTimeOffset now, int limit, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<ApprovalRequest> rows = [.. _rows.Values
                .Where(r => r.Status == ApprovalStatus.Pending && r.ExpiresAt > now)
                .OrderByDescending(r => r.RequestedAt)
                .Take(Math.Max(limit, 0))];
            return Task.FromResult(rows);
        }
    }
}
