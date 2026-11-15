using System.Security.Cryptography;
using Bazaar.Server.Identity.Keys;
using Npgsql;

namespace Bazaar.Server.Gc.Body;

/// <summary>
/// Per-person GC data keys (docs/design/04, <c>person_key</c>). Each key is 32 random bytes, wrapped under the host KEK and bound
/// to its row by the key id as associated data, so a wrapped key cannot be moved to another row and still unwrap. Keys are
/// created on first use and never deleted; erasure clears the wrapped key (see <c>erase_person_key</c>).
/// </summary>
/// <remarks>
/// Creation takes a transaction-scoped advisory lock per person, so two first writes for one person create one key. There is no
/// in-memory cache: a destroyed key must stop opening bodies at once, and a cache would keep it in memory after erasure.
/// </remarks>
public sealed class PersonKeyStore(NpgsqlDataSource dataSource, IKekProvider kek)
{
    private const int _keySize = 32;

    // Serialises first-key creation for one person; the lock is released at commit or rollback.
    private const string _lockSql = "SELECT pg_advisory_xact_lock(hashtextextended($1::text, 0))";

    private const string _activeSql = """
        SELECT key_id, wrapped_key FROM person_key
        WHERE person_id = $1 AND destroyed_at IS NULL
        ORDER BY created_at DESC, key_id DESC
        LIMIT 1
        """;

    private const string _insertSql = "INSERT INTO person_key (key_id, person_id, wrapped_key) VALUES ($1, $2, $3)";

    private const string _keySql = "SELECT wrapped_key, destroyed_at IS NOT NULL FROM person_key WHERE key_id = $1";

    /// <summary>The live key of a person and its plaintext data key.</summary>
    public sealed record PersonKey(Guid KeyId, byte[] DataKey);

    /// <summary>Returns the person's live key, creating and storing one when the person has none.</summary>
    public async ValueTask<PersonKey> GetOrCreateActiveAsync(Guid personId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var lockCmd = new NpgsqlCommand(_lockSql, conn, tx))
        {
            lockCmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = personId.ToString("D") });
            await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        Guid keyId;
        byte[] wrapped;
        await using (var find = new NpgsqlCommand(_activeSql, conn, tx))
        {
            find.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = personId });
            await using var reader = await find.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                keyId = reader.GetGuid(0);
                wrapped = reader.GetFieldValue<byte[]>(1);
                await reader.CloseAsync().ConfigureAwait(false);
                var existing = await kek.UnwrapAsync(wrapped, keyId.ToByteArray(), ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return new PersonKey(keyId, existing);
            }
        }

        keyId = Guid.CreateVersion7();
        var dataKey = RandomNumberGenerator.GetBytes(_keySize);
        wrapped = await kek.WrapAsync(dataKey, keyId.ToByteArray(), ct).ConfigureAwait(false);
        await using (var insert = new NpgsqlCommand(_insertSql, conn, tx))
        {
            insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = keyId });
            insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = personId });
            insert.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = wrapped });
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new PersonKey(keyId, dataKey);
    }

    /// <summary>
    /// Returns the data key for a stored key id, or <c>null</c> when the key was destroyed by erasure. Throws when the key id is
    /// unknown (the row is missing, which is not an erasure) or the wrap does not verify.
    /// </summary>
    public async ValueTask<byte[]?> UnwrapAsync(Guid keyId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(_keySql, conn);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = keyId });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            throw new InvalidOperationException($"No person key {keyId} exists.");

        var destroyed = reader.GetBoolean(1);
        if (destroyed)
            return null;

        var wrapped = reader.GetFieldValue<byte[]>(0);
        await reader.CloseAsync().ConfigureAwait(false);
        return await kek.UnwrapAsync(wrapped, keyId.ToByteArray(), ct).ConfigureAwait(false);
    }
}
