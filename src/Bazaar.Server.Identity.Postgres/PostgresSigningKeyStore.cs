using Bazaar.Server.Identity.Signing;
using Npgsql;

namespace Bazaar.Server.Identity.Postgres;

/// <summary>
/// Durable <see cref="ISigningKeyStore"/> over <c>soul_dek</c> (common 0019). Rotation retires the live row and inserts the new one in one
/// transaction; the partial unique index allows only one live row per scope, so a concurrent second insert fails rather than
/// leaving two live keys.
/// </summary>
public sealed class PostgresSigningKeyStore(NpgsqlDataSource dataSource) : ISigningKeyStore
{
    private const string _columns = "dek_id, scope, wrapped_dek, kek_id, created_at, retired_at";
    private const string _getLive = "SELECT " + _columns + " FROM soul_dek WHERE scope = $1 AND retired_at IS NULL";
    private const string _list = "SELECT " + _columns + " FROM soul_dek WHERE scope = $1 ORDER BY created_at";
    private const string _retireLive = "UPDATE soul_dek SET retired_at = $2 WHERE scope = $1 AND retired_at IS NULL";
    private const string _insert =
        "INSERT INTO soul_dek (dek_id, scope, wrapped_dek, kek_id, created_at) VALUES ($1, $2, $3, $4, $5)";
    private const string _clearRetired =
        "UPDATE soul_dek SET wrapped_dek = NULL WHERE scope = $1 AND retired_at IS NOT NULL AND retired_at <= $2 AND wrapped_dek IS NOT NULL";

    /// <inheritdoc />
    public async ValueTask<SigningKeyRow?> GetLiveAsync(string scope, CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand(_getLive);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = scope });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<SigningKeyRow>> ListAsync(string scope, CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand(_list);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = scope });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<SigningKeyRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(Read(reader));
        }

        return rows;
    }

    /// <inheritdoc />
    public async ValueTask RotateAsync(NewSigningKey newKey, DateTimeOffset retiredAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newKey);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var retire = new NpgsqlCommand(_retireLive, connection))
        {
            retire.Transaction = tx;
            retire.Parameters.Add(new NpgsqlParameter<string> { TypedValue = newKey.Scope });
            retire.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = retiredAt });
            await retire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var insert = new NpgsqlCommand(_insert, connection))
        {
            insert.Transaction = tx;
            insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = newKey.DekId });
            insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = newKey.Scope });
            insert.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = newKey.WrappedDek });
            insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = newKey.KekId });
            insert.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = newKey.CreatedAt });
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ClearRetiredWrapsAsync(string scope, DateTimeOffset retiredOnOrBefore, CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand(_clearRetired);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = scope });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = retiredOnOrBefore });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SigningKeyRow Read(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetFieldValue<byte[]>(2),
            reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5));
}
