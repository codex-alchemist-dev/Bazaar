using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Identity.Kek;

/// <summary>
/// The Postgres re-wrap store over <c>person_key</c> (GC per-person data keys, docs/design/04). A row's associated data is its
/// key id, the same binding <c>PersonKeyStore</c> uses. Replacement is one conditional UPDATE, so it commits only while the
/// stored wrap is still the one that was read.
/// </summary>
public sealed class PostgresKekRewrapStore(NpgsqlDataSource dataSource) : IKekRewrapStore
{
    private const string _listSql = """
        SELECT key_id, wrapped_key FROM person_key
        WHERE destroyed_at IS NULL AND wrapped_key IS NOT NULL AND ($1::uuid IS NULL OR key_id > $1)
        ORDER BY key_id
        LIMIT $2
        """;

    private const string _replaceSql = """
        UPDATE person_key SET wrapped_key = $3
        WHERE key_id = $1 AND wrapped_key = $2 AND destroyed_at IS NULL
        """;

    /// <inheritdoc />
    public async Task<IReadOnlyList<KekWrapRecord>> ListLiveWrapsAsync(Guid? afterId, int limit, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(_listSql);
        command.Parameters.Add(new NpgsqlParameter<Guid?> { TypedValue = afterId, NpgsqlDbType = NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });

        var rows = new List<KekWrapRecord>(limit);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = reader.GetGuid(0);
            rows.Add(new KekWrapRecord(id, reader.GetFieldValue<byte[]>(1), id.ToByteArray()));
        }

        return rows;
    }

    /// <inheritdoc />
    public async Task<bool> ReplaceWrapAsync(Guid id, ReadOnlyMemory<byte> expected, ReadOnlyMemory<byte> replacement, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(_replaceSql);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id, NpgsqlDbType = NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = expected.ToArray(), NpgsqlDbType = NpgsqlDbType.Bytea });
        command.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = replacement.ToArray(), NpgsqlDbType = NpgsqlDbType.Bytea });

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }
}
