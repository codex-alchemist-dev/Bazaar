using Bazaar.Server.Identity.Kek;

namespace Bazaar.Server.ColumnSeal;

/// <summary>A <see cref="IColumnDekStore"/> that keeps its rows in memory. For tests and tools; nothing survives a restart.</summary>
public sealed class InMemoryColumnDekStore(IKekProvider kek, ColumnDekStoreOptions? options = null, TimeProvider? timeProvider = null)
    : ColumnDekStoreBase(kek, options, timeProvider)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, StoredDek> _rows = [];

    /// <summary>Replaces a stored row, so a test can simulate an edited database row.</summary>
    internal void ReplaceRowForTests(StoredDek row)
    {
        lock (_gate)
            _rows[row.DekId] = row;
    }

    internal StoredDek? RowForTests(Guid dekId)
    {
        lock (_gate)
            return _rows.GetValueOrDefault(dekId);
    }

    protected override ValueTask<StoredDek?> LoadAsync(Guid dekId, CancellationToken cancellationToken)
    {
        lock (_gate)
            return ValueTask.FromResult(_rows.GetValueOrDefault(dekId));
    }

    protected override ValueTask<StoredDek?> LoadActiveAsync(string scope, CancellationToken cancellationToken)
    {
        lock (_gate)
            return ValueTask.FromResult(_rows.Values.FirstOrDefault(r => r.Scope == scope && r.RetiredAt is null));
    }

    protected override ValueTask<bool> TryInsertAsync(StoredDek dek, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_rows.Values.Any(r => r.Scope == dek.Scope && r.RetiredAt is null))
                return ValueTask.FromResult(false);
            _rows[dek.DekId] = dek;
            return ValueTask.FromResult(true);
        }
    }

    protected override ValueTask<bool> MarkRetiredAsync(Guid dekId, DateTimeOffset retiredAt, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(dekId, out var row) || row.RetiredAt is not null)
                return ValueTask.FromResult(false);
            _rows[dekId] = row with { RetiredAt = retiredAt };
            return ValueTask.FromResult(true);
        }
    }
}
