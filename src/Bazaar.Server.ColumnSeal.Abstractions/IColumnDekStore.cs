using System.Security.Cryptography;

namespace Bazaar.Server.ColumnSeal;

/// <summary>
/// An unwrapped data-encryption key. Dispose clears the bytes. The key is this holder's own copy, so disposing it never
/// affects the store's cache.
/// </summary>
public sealed class ColumnDek : IDisposable
{
    private readonly byte[] _key;

    public ColumnDek(string scope, Guid dekId, string kekId, ReadOnlySpan<byte> key, bool retired)
    {
        Scope = scope;
        DekId = dekId;
        KekId = kekId;
        Retired = retired;
        _key = key.ToArray();
    }

    /// <summary>The scope the key belongs to.</summary>
    public string Scope { get; }

    /// <summary>The key id, stored beside every value sealed under it.</summary>
    public Guid DekId { get; }

    /// <summary>The KEK that wrapped this key, so rotation can find old wraps.</summary>
    public string KekId { get; }

    /// <summary>True once the key was retired: it still opens old values but is never used for new seals.</summary>
    public bool Retired { get; }

    /// <summary>The 32 key bytes. Valid until <see cref="Dispose"/>.</summary>
    public ReadOnlySpan<byte> Key => _key;

    /// <inheritdoc />
    public void Dispose() => CryptographicOperations.ZeroMemory(_key);
}

/// <summary>Holds the wrapped DEKs, one live key per scope (doc 63 section 9, <c>column_dek</c>).</summary>
public interface IColumnDekStore
{
    /// <summary>The scope's live DEK; creates one (32 random bytes, wrapped through the KEK) when the scope has none or only retired ones.</summary>
    ValueTask<ColumnDek> GetOrCreateActiveAsync(string scope, CancellationToken cancellationToken);

    /// <summary>A DEK by id, retired or not; null when no such key exists.</summary>
    ValueTask<ColumnDek?> GetAsync(Guid dekId, CancellationToken cancellationToken);

    /// <summary>Retires a key. Old values still open; the next <see cref="GetOrCreateActiveAsync"/> makes a new key. False when it was unknown or already retired.</summary>
    ValueTask<bool> RetireAsync(Guid dekId, CancellationToken cancellationToken);
}
