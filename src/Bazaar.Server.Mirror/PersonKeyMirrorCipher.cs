using System.Security.Cryptography;
using Bazaar.Server.Identity.Kek;
using Npgsql;

namespace Bazaar.Server.Mirror;

/// <summary>
/// The host cipher for Mirror (doc 04 per-person keys, doc 26 section 5). Mirror is owner-scope, so values are sealed under the
/// owner's live <c>person_key</c> data key, wrapped under the KEK with the key id as associated data (the same wrap the GC bodies use,
/// so erasure of the owner's keys makes Mirror unreadable too). Nothing is cached: a destroyed key stops opening values at once.
/// It fails closed: with no owner, no KEK or a destroyed key it throws and never returns plaintext.
/// </summary>
/// <remarks>
/// PROPOSED (doc 26 section 5): the owner is the <c>human</c> row with role <c>owner</c> and status <c>active</c>. Bazaar.Server's
/// key store is not reachable from a module, so this reads and writes <c>person_key</c> with the same rows and the same wrap.
/// </remarks>
public sealed class PersonKeyMirrorCipher(NpgsqlDataSource dataSource, IKekProvider kek) : IMirrorCipher
{
    private const string _ownerSql = "SELECT id FROM human WHERE role = 'owner' AND status = 'active' LIMIT 1";
    private const string _lockSql = "SELECT pg_advisory_xact_lock(hashtextextended($1::text, 0))";
    private const string _activeSql = """
        SELECT key_id, wrapped_key FROM person_key
        WHERE person_id = $1 AND destroyed_at IS NULL
        ORDER BY created_at DESC, key_id DESC LIMIT 1
        """;
    private const string _insertSql = "INSERT INTO person_key (key_id, person_id, wrapped_key) VALUES ($1, $2, $3)";
    private const string _keySql = "SELECT wrapped_key FROM person_key WHERE key_id = $1 AND destroyed_at IS NULL";

    public async ValueTask<SealedMirrorValue> SealAsync(string plaintext, byte[] associatedData, CancellationToken cancellationToken)
    {
        var (keyId, key) = await GetOrCreateOwnerKeyAsync(cancellationToken).ConfigureAwait(false);
        return AesGcmMirrorCipher.Seal(keyId, key, plaintext, associatedData);
    }

    public async ValueTask<string> OpenAsync(SealedMirrorValue value, byte[] associatedData, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        await using var cmd = dataSource.CreateCommand(_keySql);
        cmd.Parameters.AddWithValue(value.KeyId);
        var wrapped = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as byte[]
            ?? throw new CryptographicException("The Mirror key was destroyed or does not exist.");
        var key = await kek.UnwrapAsync(wrapped, value.KeyId.ToByteArray(), cancellationToken).ConfigureAwait(false);
        return AesGcmMirrorCipher.Open(key, value, associatedData);
    }

    private async Task<(Guid KeyId, byte[] Key)> GetOrCreateOwnerKeyAsync(CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        Guid owner;
        await using (var ownerCmd = new NpgsqlCommand(_ownerSql, conn, tx))
        {
            owner = await ownerCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is Guid g
                ? g
                : throw new InvalidOperationException("Mirror needs an owner and the install has none.");
        }

        await using (var lockCmd = new NpgsqlCommand(_lockSql, conn, tx))
        {
            lockCmd.Parameters.AddWithValue(owner.ToString("D"));
            await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var find = new NpgsqlCommand(_activeSql, conn, tx))
        {
            find.Parameters.AddWithValue(owner);
            await using var reader = await find.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var existingId = reader.GetGuid(0);
                var wrapped = reader.GetFieldValue<byte[]>(1);
                await reader.CloseAsync().ConfigureAwait(false);
                var existing = await kek.UnwrapAsync(wrapped, existingId.ToByteArray(), ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return (existingId, existing);
            }
        }

        var keyId = Guid.CreateVersion7();
        var dataKey = RandomNumberGenerator.GetBytes(32);
        var newWrapped = await kek.WrapAsync(dataKey, keyId.ToByteArray(), ct).ConfigureAwait(false);
        await using (var insert = new NpgsqlCommand(_insertSql, conn, tx))
        {
            insert.Parameters.AddWithValue(keyId);
            insert.Parameters.AddWithValue(owner);
            insert.Parameters.AddWithValue(newWrapped);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return (keyId, dataKey);
    }
}
