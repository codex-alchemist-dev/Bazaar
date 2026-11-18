namespace Bazaar.Server.Identity.Signing;

/// <summary>
/// An <see cref="ISigningKeyStore"/> in process memory, for tests and Development. Keys do not survive a restart. Enforces the same rules as
/// the Postgres store: one live row per scope, and a clear keeps the row but drops its wrap.
/// </summary>
public sealed class InMemorySigningKeyStore : ISigningKeyStore
{
    private readonly Lock _gate = new();
    private readonly List<SigningKeyRow> _rows = [];

    /// <inheritdoc />
    public ValueTask<SigningKeyRow?> GetLiveAsync(string scope, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_rows.FirstOrDefault(r => r.Scope == scope && r.RetiredAt is null));
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SigningKeyRow>> ListAsync(string scope, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<SigningKeyRow> rows = _rows.Where(r => r.Scope == scope).Select(CopyRow).ToList();
            return ValueTask.FromResult(rows);
        }
    }

    /// <inheritdoc />
    public ValueTask RotateAsync(NewSigningKey newKey, DateTimeOffset retiredAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newKey);
        lock (_gate)
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (row.Scope == newKey.Scope && row.RetiredAt is null)
                {
                    _rows[i] = row with { RetiredAt = retiredAt };
                }
            }

            _rows.Add(new SigningKeyRow(newKey.DekId, newKey.Scope, newKey.WrappedDek, newKey.KekId, newKey.CreatedAt, null));
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask ClearRetiredWrapsAsync(string scope, DateTimeOffset retiredOnOrBefore, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (row.Scope == scope && row.RetiredAt is { } retired && retired <= retiredOnOrBefore && row.WrappedDek is not null)
                {
                    _rows[i] = row with { WrappedDek = null };
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Copies a row so a caller cannot change the stored wrap through the bytes it was handed.</summary>
    private static SigningKeyRow CopyRow(SigningKeyRow row) =>
        row with { WrappedDek = row.WrappedDek is null ? null : (byte[])row.WrappedDek.Clone() };
}
