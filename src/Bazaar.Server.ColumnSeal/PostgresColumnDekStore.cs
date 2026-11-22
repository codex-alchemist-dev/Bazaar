using Bazaar.Server.Identity.Kek;
using Npgsql;

namespace Bazaar.Server.ColumnSeal;

/// <summary>The PostgreSQL <see cref="IColumnDekStore"/>: raw Npgsql over the shared data source, table <c>column_dek</c> (migration <c>column_seal/0001</c>).</summary>
public sealed class PostgresColumnDekStore(NpgsqlDataSource dataSource, IKekProvider kek, ColumnDekStoreOptions? options = null, TimeProvider? timeProvider = null)
    : ColumnDekStoreBase(kek, options, timeProvider)
{
    private const string _columns = "scope, dek_id, wrapped_dek, kek_id, created_at, retired_at";

    protected override async ValueTask<StoredDek?> LoadAsync(Guid dekId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {_columns} FROM column_dek WHERE dek_id = $1");
        cmd.Parameters.AddWithValue(dekId);
        return await ReadOneAsync(cmd, cancellationToken).ConfigureAwait(false);
    }

    protected override async ValueTask<StoredDek?> LoadActiveAsync(string scope, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {_columns} FROM column_dek WHERE scope = $1 AND retired_at IS NULL");
        cmd.Parameters.AddWithValue(scope);
        return await ReadOneAsync(cmd, cancellationToken).ConfigureAwait(false);
    }

    protected override async ValueTask<bool> TryInsertAsync(StoredDek dek, CancellationToken cancellationToken)
    {
        // The partial unique index allows one live key per scope; a lost race inserts nothing.
        await using var cmd = dataSource.CreateCommand(
            "INSERT INTO column_dek (dek_id, scope, wrapped_dek, kek_id, created_at) VALUES ($1, $2, $3, $4, $5) ON CONFLICT DO NOTHING");
        cmd.Parameters.AddWithValue(dek.DekId);
        cmd.Parameters.AddWithValue(dek.Scope);
        cmd.Parameters.AddWithValue(dek.WrappedDek);
        cmd.Parameters.AddWithValue(dek.KekId);
        cmd.Parameters.AddWithValue(dek.CreatedAt.ToUniversalTime());
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    protected override async ValueTask<bool> MarkRetiredAsync(Guid dekId, DateTimeOffset retiredAt, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("UPDATE column_dek SET retired_at = $2 WHERE dek_id = $1 AND retired_at IS NULL");
        cmd.Parameters.AddWithValue(dekId);
        cmd.Parameters.AddWithValue(retiredAt.ToUniversalTime());
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static async ValueTask<StoredDek?> ReadOneAsync(NpgsqlCommand cmd, CancellationToken cancellationToken)
    {
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return new StoredDek(
            reader.GetString(0),
            reader.GetGuid(1),
            reader.GetFieldValue<byte[]>(2),
            reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5));
    }
}
