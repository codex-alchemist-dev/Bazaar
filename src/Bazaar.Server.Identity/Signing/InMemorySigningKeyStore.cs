namespace Bazaar.Server.Identity.Signing;

/// <summary>
/// An <see cref="ISigningKeyStore"/> in process memory, for tests and Development. Keys do not survive a restart. Enforces the same rules as
/// the Postgres store: one live row per scope, and a clear keeps the row but drops its wrap.
/// </summary>
public sealed class InMemorySigningKeyStore : ISigningKeyStore
{
    private readonly Lock _gate = new();
    private readonly List<SigningKeyRow> _rows = [];
    private NewSigningKey? _competitor;

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

    /// <summary>
    /// Test seam: makes the next <see cref="RotateAsync"/> lose a race. <paramref name="competitor"/> is committed by another host first,
    /// so the next rotation inserts nothing and returns that live row, as the Postgres store does on SQLSTATE 23505.
    /// </summary>
    public void ArrangeLostRace(NewSigningKey competitor)
    {
        ArgumentNullException.ThrowIfNull(competitor);
        lock (_gate)
        {
            _competitor = competitor;
        }
    }

    /// <inheritdoc />
    public ValueTask<SigningKeyRow> RotateAsync(NewSigningKey newKey, DateTimeOffset retiredAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newKey);
        lock (_gate)
        {
            if (_competitor is { } competitor)
            {
                // The other host's rotation is committed first. Our insert would break the one-live rule, so we retire nothing and
                // return the winning live row.
                _competitor = null;
                RetireLive(competitor.Scope, competitor.CreatedAt);
                var winner = new SigningKeyRow(competitor.DekId, competitor.Scope, competitor.WrappedDek, competitor.KekId, competitor.CreatedAt, null);
                _rows.Add(winner);
                return ValueTask.FromResult(CopyRow(winner));
            }

            RetireLive(newKey.Scope, retiredAt);
            var row = new SigningKeyRow(newKey.DekId, newKey.Scope, newKey.WrappedDek, newKey.KekId, newKey.CreatedAt, null);
            _rows.Add(row);
            return ValueTask.FromResult(CopyRow(row));
        }
    }

    /// <summary>Retires the scope's live rows. Called under the gate.</summary>
    private void RetireLive(string scope, DateTimeOffset retiredAt)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            if (row.Scope == scope && row.RetiredAt is null)
            {
                _rows[i] = row with { RetiredAt = retiredAt };
            }
        }
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
