using System.Security.Cryptography;
using Bazaar.Server.Identity.Kek;

namespace Bazaar.Server.ColumnSeal;

/// <summary>One <c>column_dek</c> row: a DEK as it is stored, wrapped under the KEK.</summary>
public sealed record StoredDek(string Scope, Guid DekId, byte[] WrappedDek, string KekId, DateTimeOffset CreatedAt, DateTimeOffset? RetiredAt);

/// <summary>Tuning for a DEK store.</summary>
public sealed class ColumnDekStoreOptions
{
    /// <summary>How many unwrapped DEKs stay in memory. One per scope in use is enough; the default is generous.</summary>
    public int CacheSize { get; init; } = 64;

    /// <summary>How long a scope's live key id is trusted before the store asks again, so a retirement by another process is seen.</summary>
    public TimeSpan ActiveKeyTtl { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The wrap, unwrap, cache and create-if-none logic shared by every <see cref="IColumnDekStore"/>. Subclasses only persist
/// <see cref="StoredDek"/> rows. DEKs are 32 random bytes; the KEK wraps them with associated data that binds scope and dek id,
/// so a wrapped key moved to another scope or id does not unwrap. A cached retired flag can lag a retirement made by another
/// process; sealing never depends on it, because the live key is looked up again after <see cref="ColumnDekStoreOptions.ActiveKeyTtl"/>.
/// </summary>
public abstract class ColumnDekStoreBase : IColumnDekStore, IDisposable
{
    private const int _dekSize = 32;

    private readonly IKekProvider _kek;
    private readonly DekCache _cache;
    private readonly TimeProvider _time;
    private readonly TimeSpan _activeTtl;
    private readonly Lock _activeGate = new();
    private readonly Dictionary<string, (Guid DekId, DateTimeOffset Seen)> _active = [];

    protected ColumnDekStoreBase(IKekProvider kek, ColumnDekStoreOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(kek);
        options ??= new ColumnDekStoreOptions();
        _kek = kek;
        _cache = new DekCache(options.CacheSize);
        _activeTtl = options.ActiveKeyTtl;
        _time = timeProvider ?? TimeProvider.System;
    }

    internal DekCache Cache => _cache;

    /// <summary>The stored row for a key id, or null.</summary>
    protected abstract ValueTask<StoredDek?> LoadAsync(Guid dekId, CancellationToken cancellationToken);

    /// <summary>The scope's live (not retired) row, or null.</summary>
    protected abstract ValueTask<StoredDek?> LoadActiveAsync(string scope, CancellationToken cancellationToken);

    /// <summary>Inserts a live row. False, with nothing stored, when the scope already has a live row (another writer won).</summary>
    protected abstract ValueTask<bool> TryInsertAsync(StoredDek dek, CancellationToken cancellationToken);

    /// <summary>Marks a live row retired. False when the row is unknown or already retired.</summary>
    protected abstract ValueTask<bool> MarkRetiredAsync(Guid dekId, DateTimeOffset retiredAt, CancellationToken cancellationToken);

    /// <inheritdoc />
    public async ValueTask<ColumnDek> GetOrCreateActiveAsync(string scope, CancellationToken cancellationToken)
    {
        ColumnSealNames.ValidateName(scope, "scope");

        if (TryGetActiveId(scope, out var activeId) && _cache.TryGet(activeId) is { } cached)
        {
            if (!cached.Retired)
                return cached;
            cached.Dispose();
        }

        var stored = await LoadActiveAsync(scope, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            var created = await CreateAsync(scope, cancellationToken).ConfigureAwait(false);
            if (created is not null)
                return created;

            // Another writer created the live key between our read and our insert; use theirs.
            stored = await LoadActiveAsync(scope, cancellationToken).ConfigureAwait(false)
                ?? throw new ColumnSealException(ColumnSealFailure.KeyUnavailable, "No data key is available for the scope.");
        }

        var dek = await OpenStoredAsync(stored, cancellationToken).ConfigureAwait(false);
        RememberActive(scope, dek.DekId);
        return dek;
    }

    /// <inheritdoc />
    public async ValueTask<ColumnDek?> GetAsync(Guid dekId, CancellationToken cancellationToken)
    {
        if (_cache.TryGet(dekId) is { } cached)
            return cached;

        var stored = await LoadAsync(dekId, cancellationToken).ConfigureAwait(false);
        return stored is null ? null : await OpenStoredAsync(stored, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> RetireAsync(Guid dekId, CancellationToken cancellationToken)
    {
        var retired = await MarkRetiredAsync(dekId, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        // Drop the cached entry (its flag is now stale) and the active pointer, so the next lookup reads the store.
        _cache.Remove(dekId);
        lock (_activeGate)
        {
            foreach (var scope in _active.Where(p => p.Value.DekId == dekId).Select(p => p.Key).ToList())
                _active.Remove(scope);
        }

        return retired;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Zeroes every cached key.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
            _cache.Dispose();
    }

    private async ValueTask<ColumnDek?> CreateAsync(string scope, CancellationToken cancellationToken)
    {
        var key = RandomNumberGenerator.GetBytes(_dekSize);
        try
        {
            var dekId = Guid.CreateVersion7();
            var wrapped = await WrapAsync(key, scope, dekId, cancellationToken).ConfigureAwait(false);
            var row = new StoredDek(scope, dekId, wrapped, _kek.KekId, _time.GetUtcNow(), RetiredAt: null);
            if (!await TryInsertAsync(row, cancellationToken).ConfigureAwait(false))
                return null;

            var dek = new ColumnDek(scope, dekId, row.KekId, key, retired: false);
            _cache.Add(dek);
            RememberActive(scope, dekId);
            return dek;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private async ValueTask<byte[]> WrapAsync(byte[] key, string scope, Guid dekId, CancellationToken cancellationToken)
    {
        try
        {
            return await _kek.WrapAsync(key, ColumnSealNames.BuildDekAad(scope, dekId), cancellationToken).ConfigureAwait(false);
        }
        catch (KekUnwrapException ex)
        {
            throw new ColumnSealException(ColumnSealFailure.KeyUnavailable, "The data key could not be wrapped.", ex);
        }
    }

    private async ValueTask<ColumnDek> OpenStoredAsync(StoredDek stored, CancellationToken cancellationToken)
    {
        if (_cache.TryGet(stored.DekId) is { } cached)
        {
            // The cache holds the key; the row is the authority on the retired flag. A scope mismatch means the row was
            // altered, so the key is unwrapped again below, where the associated data will not match.
            if (cached.Scope == stored.Scope)
            {
                if (cached.Retired == (stored.RetiredAt is not null))
                    return cached;
                var key = cached.Key.ToArray();
                cached.Dispose();
                return Remember(stored, key);
            }

            cached.Dispose();
        }

        byte[] plain;
        try
        {
            plain = await _kek.UnwrapAsync(stored.WrappedDek, ColumnSealNames.BuildDekAad(stored.Scope, stored.DekId), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (KekUnwrapException ex)
        {
            throw new ColumnSealException(ColumnSealFailure.KeyUnavailable, "The data key could not be unwrapped.", ex);
        }

        if (plain.Length != _dekSize)
        {
            CryptographicOperations.ZeroMemory(plain);
            throw new ColumnSealException(ColumnSealFailure.KeyUnavailable, "The data key could not be unwrapped.");
        }

        return Remember(stored, plain);
    }

    private ColumnDek Remember(StoredDek stored, byte[] plain)
    {
        try
        {
            var dek = new ColumnDek(stored.Scope, stored.DekId, stored.KekId, plain, stored.RetiredAt is not null);
            _cache.Add(dek);
            return dek;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private bool TryGetActiveId(string scope, out Guid id)
    {
        lock (_activeGate)
        {
            if (_active.TryGetValue(scope, out var seen) && _time.GetUtcNow() - seen.Seen < _activeTtl)
            {
                id = seen.DekId;
                return true;
            }
        }

        id = Guid.Empty;
        return false;
    }

    private void RememberActive(string scope, Guid dekId)
    {
        lock (_activeGate)
            _active[scope] = (dekId, _time.GetUtcNow());
    }
}
