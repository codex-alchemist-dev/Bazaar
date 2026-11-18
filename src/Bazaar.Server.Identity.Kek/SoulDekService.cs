using System.Security.Cryptography;

namespace Bazaar.Server.Identity.Kek;

/// <summary>An opened data key. Dispose it to zero the key bytes as soon as the caller is done.</summary>
public sealed class SoulDek(Guid dekId, byte[] key) : IDisposable
{
    public Guid DekId { get; } = dekId;

    public ReadOnlyMemory<byte> Key => key;

    public void Dispose() => CryptographicOperations.ZeroMemory(key);
}

/// <summary>
/// Data-key custody for doc 14: one DEK per Soul, plus an installation-scope DEK for provider and App keys. DEKs are 256-bit random
/// keys wrapped under the host KEK with the DEK id as associated data, so a wrap cannot be moved to another row. Rotation of the KEK
/// is a re-wrap (<see cref="KekRewriter"/>); this service never re-encrypts rows. Soul deletion calls <see cref="ShredAsync"/>.
/// </summary>
public sealed class SoulDekService(IKekProvider kek, ISoulDekStore store, IKekAuditSink? audit = null)
{
    public const string ShreddedAuditKind = "identity.dek.shredded";

    /// <summary>Returns the live DEK for the scope, creating it first when the scope has none. Safe under concurrent callers.</summary>
    public async Task<SoulDek> GetOrCreateAsync(string scope, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var existing = await store.GetLiveAsync(scope, ct).ConfigureAwait(false);
            if (existing is not null)
                return await OpenAsync(existing, ct).ConfigureAwait(false);

            var dekId = Guid.CreateVersion7();
            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                var wrapped = await kek.WrapAsync(key, dekId.ToByteArray(), ct).ConfigureAwait(false);
                if (await store.TryInsertLiveAsync(dekId, scope, wrapped, kek.KekId, ct).ConfigureAwait(false))
                    return new SoulDek(dekId, (byte[])key.Clone());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

            // Another caller created the live DEK first: read theirs on the next pass.
        }

        throw new InvalidOperationException($"Could not create or read the data key for scope '{scope}'.");
    }

    /// <summary>Opens the live DEK for the scope, or returns null when it has none (never created, or crypto-shredded).</summary>
    public async Task<SoulDek?> OpenAsync(string scope, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var row = await store.GetLiveAsync(scope, ct).ConfigureAwait(false);
        return row is null ? null : await OpenAsync(row, ct).ConfigureAwait(false);
    }

    /// <summary>Crypto-shreds the scope's DEK: ciphertext under it is unreadable from now on. Audited by id only. Returns false when nothing was live.</summary>
    public async Task<bool> ShredAsync(string scope, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var retired = await store.ShredAsync(scope, ct).ConfigureAwait(false);
        if (retired is null)
            return false;

        if (audit is not null)
            await audit.RecordAsync(ShreddedAuditKind, $"{{\"dek_id\":\"{retired.Value}\"}}", ct).ConfigureAwait(false);
        return true;
    }

    private async Task<SoulDek> OpenAsync(SoulDekRecord row, CancellationToken ct)
    {
        var key = await kek.UnwrapAsync(row.Wrapped, row.DekId.ToByteArray(), ct).ConfigureAwait(false);
        return new SoulDek(row.DekId, key);
    }
}
