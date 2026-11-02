using Npgsql;

namespace Bazaar.Server.Components.Erasure;

/// <summary>
/// Calls the <c>erase_person_key(uuid)</c> function from migration 0008 for each live key of a person. Only the
/// <c>bazaar_erasure</c> role may execute that function, so the keys are listed under the session's own role (which may read
/// <c>person_key</c>), then the calls run under <c>SET LOCAL ROLE bazaar_erasure</c> inside one transaction. A session that is
/// not a member of that role fails the call, and the failure reaches the erasure report.
/// </summary>
/// <remarks>
/// Legal sufficiency of key destruction as erasure is OPEN and needs counsel (docs/design/32, section 9). This class only
/// performs the mechanical step; it makes no legal determination.
/// </remarks>
public sealed class PostgresPersonKeyEraser(NpgsqlDataSource dataSource) : IPersonKeyEraser
{
    private const string _listSql = "SELECT key_id, destroyed_at IS NOT NULL FROM person_key WHERE person_id = $1 ORDER BY created_at, key_id";
    private const string _setRoleSql = "SET LOCAL ROLE bazaar_erasure";
    private const string _eraseSql = "SELECT erase_person_key($1)";

    public async Task<PersonKeyEraseResult> EraseAsync(Guid personId, CancellationToken ct = default)
    {
        if (personId == Guid.Empty)
            throw new ArgumentException("A person id is required.", nameof(personId));

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var keys = new List<(Guid KeyId, bool Destroyed)>();
        await using (var list = new NpgsqlCommand(_listSql, conn, tx))
        {
            list.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = personId });
            await using var reader = await list.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                keys.Add((reader.GetGuid(0), reader.GetBoolean(1)));
        }

        var destroyed = 0;
        var already = keys.Count(k => k.Destroyed);
        if (keys.Exists(k => !k.Destroyed))
        {
            await using (var role = new NpgsqlCommand(_setRoleSql, conn, tx))
                await role.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            foreach (var key in keys.Where(k => !k.Destroyed))
            {
                await using var erase = new NpgsqlCommand(_eraseSql, conn, tx);
                erase.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = key.KeyId });
                // The function returns false when the key was destroyed by another session after the list was read.
                var destroyedNow = (bool)(await erase.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
                if (destroyedNow)
                    destroyed++;
                else
                    already++;
            }
        }

        // One transaction: a failure in any call rolls back the calls before it, so no key is half-destroyed.
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new PersonKeyEraseResult(destroyed, already);
    }
}
