using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Identity.Kek;

/// <summary>
/// The Postgres re-wrap store over <c>soul_dek</c> (doc 14: per-Soul and install-scope data keys). A wrap's associated data is its
/// <c>dek_id</c> bytes, the same binding <see cref="SoulDekService"/> uses when it wraps a new DEK. Retired (crypto-shredded) rows
/// hold no wrap and are never listed. Replacement is one conditional UPDATE that also records the new <c>kek_id</c>.
/// </summary>
public sealed class PostgresSoulDekRewrapStore(NpgsqlDataSource dataSource) : IKekRewrapStore
{
    private const string _listSql = """
        SELECT dek_id, wrapped_dek FROM soul_dek
        WHERE retired_at IS NULL AND wrapped_dek IS NOT NULL AND ($1::uuid IS NULL OR dek_id > $1)
        ORDER BY dek_id
        LIMIT $2
        """;

    private const string _replaceSql = """
        UPDATE soul_dek SET wrapped_dek = $3, kek_id = $4
        WHERE dek_id = $1 AND wrapped_dek = $2 AND retired_at IS NULL
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
    public async Task<bool> ReplaceWrapAsync(Guid id, ReadOnlyMemory<byte> expected, ReadOnlyMemory<byte> replacement, string kekId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kekId);
        await using var command = dataSource.CreateCommand(_replaceSql);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id, NpgsqlDbType = NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = expected.ToArray(), NpgsqlDbType = NpgsqlDbType.Bytea });
        command.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = replacement.ToArray(), NpgsqlDbType = NpgsqlDbType.Bytea });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = kekId, NpgsqlDbType = NpgsqlDbType.Text });

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }
}
