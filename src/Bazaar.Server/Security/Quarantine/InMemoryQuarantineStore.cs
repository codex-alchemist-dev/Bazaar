namespace Bazaar.Server.Security.Quarantine;

/// <summary>
/// Process-local <see cref="IQuarantineStore"/> for tests and single-process use. Thread-safe.
/// </summary>
// TODO(component): Postgres-backed store once the component model lands; the durable table belongs with the audit
// and GC schema (docs 01, 04).
public sealed class InMemoryQuarantineStore : IQuarantineStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, QuarantineRecord> _rows = [];

    /// <inheritdoc />
    public Task<QuarantineRecord?> GetAsync(Guid id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.GetValueOrDefault(id));
        }
    }

    /// <inheritdoc />
    public Task<QuarantineRecord?> GetActiveAsync(QuarantineSubject subject, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        lock (_gate)
        {
            return Task.FromResult(_rows.Values.FirstOrDefault(r => r.Status == QuarantineStatus.Quarantined && r.Subject == subject));
        }
    }

    /// <inheritdoc />
    public Task<bool> TryInsertActiveAsync(QuarantineRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            var active = _rows.Values.Any(r => r.Status == QuarantineStatus.Quarantined && r.Subject == record.Subject);
            if (active || !_rows.TryAdd(record.Id, record))
            {
                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<QuarantineRecord?> TryReleaseAsync(Guid id, QuarantineActor releasedBy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(releasedBy);
        lock (_gate)
        {
            if (!_rows.TryGetValue(id, out var current) || current.Status != QuarantineStatus.Quarantined)
            {
                return Task.FromResult<QuarantineRecord?>(null);
            }

            var released = current with { ReleasedBy = releasedBy };
            _rows[id] = released;
            return Task.FromResult<QuarantineRecord?>(released);
        }
    }
}
