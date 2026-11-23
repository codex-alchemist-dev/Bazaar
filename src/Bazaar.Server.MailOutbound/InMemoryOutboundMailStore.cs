namespace Bazaar.Server.MailOutbound;

/// <summary>
/// In-memory <see cref="IOutboundMailStore"/> for tests and for hosts that run without a database. It forgets everything when the process ends, so it
/// gives no restart durability: a host that sends real mail registers <see cref="PostgresOutboundMailStore"/> instead. Like the real store it
/// drops the stored text when a send ends in Failed, Rejected or Expired.
/// </summary>
public sealed class InMemoryOutboundMailStore : IOutboundMailStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, StoredOutbound> _rows = new(StringComparer.Ordinal);

    public Task<StoredOutbound?> FindAsync(string idempotencyKey, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.TryGetValue(idempotencyKey, out var row) ? row : null);
        }
    }

    public Task<(StoredOutbound Record, bool Created)> TryInsertAsync(StoredOutbound record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            if (_rows.TryGetValue(record.IdempotencyKey, out var existing)) return Task.FromResult((existing, false));
            _rows[record.IdempotencyKey] = record;
            return Task.FromResult((record, true));
        }
    }

    public Task<bool> TryUpdateAsync(StoredOutbound expected, StoredOutbound updated, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(updated);
        lock (_gate)
        {
            if (!_rows.TryGetValue(expected.IdempotencyKey, out var current) || current.Status != expected.Status || current.Attempts != expected.Attempts)
            {
                return Task.FromResult(false);
            }

            _rows[expected.IdempotencyKey] = updated.Status is OutboundStatus.Failed or OutboundStatus.Rejected or OutboundStatus.Expired
                ? updated with { Request = null }
                : updated;
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<string>> ListDueAsync(DateTimeOffset now, DateTimeOffset staleSendingBefore, int max, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<string> keys = [.. _rows.Values
                .Where(r => r.Status == OutboundStatus.AwaitingApproval
                    || (r.Status == OutboundStatus.Pending && r.NextAttemptAt <= now)
                    || (r.Status == OutboundStatus.Sending && r.UpdatedAt <= staleSendingBefore))
                .OrderBy(r => r.UpdatedAt)
                .Take(Math.Max(0, max))
                .Select(r => r.IdempotencyKey)];
            return Task.FromResult(keys);
        }
    }
}
