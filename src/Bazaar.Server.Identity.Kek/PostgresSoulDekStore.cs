using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.Identity.Kek;

/// <summary>The Postgres <see cref="ISoulDekStore"/> over <c>soul_dek</c> (migration <c>common/0019_identity_keys.sql</c>).</summary>
public sealed class PostgresSoulDekStore(NpgsqlDataSource dataSource) : ISoulDekStore
{
    private const string _uniqueViolation = "23505";

    /// <inheritdoc />
    public async Task<SoulDekRecord?> GetLiveAsync(string scope, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT dek_id, wrapped_dek, kek_id FROM soul_dek WHERE scope = $1 AND retired_at IS NULL");
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = scope, NpgsqlDbType = NpgsqlDbType.Text });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? new SoulDekRecord(reader.GetGuid(0), reader.GetFieldValue<byte[]>(1), reader.GetString(2))
            : null;
    }

    /// <inheritdoc />
    public async Task<bool> TryInsertLiveAsync(Guid dekId, string scope, byte[] wrapped, string kekId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            "INSERT INTO soul_dek (dek_id, scope, wrapped_dek, kek_id) VALUES ($1, $2, $3, $4)");
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = dekId, NpgsqlDbType = NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = scope, NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = wrapped, NpgsqlDbType = NpgsqlDbType.Bytea });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = kekId, NpgsqlDbType = NpgsqlDbType.Text });
        try
        {
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == _uniqueViolation)
        {
            return false; // The one-live-DEK-per-scope index: another caller created it first.
        }
    }

    /// <inheritdoc />
    public async Task<Guid?> ShredAsync(string scope, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE soul_dek SET retired_at = now(), wrapped_dek = NULL WHERE scope = $1 AND retired_at IS NULL RETURNING dek_id");
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = scope, NpgsqlDbType = NpgsqlDbType.Text });
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is Guid id ? id : null;
    }
}
