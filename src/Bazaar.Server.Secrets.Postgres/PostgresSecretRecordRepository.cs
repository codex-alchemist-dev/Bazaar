using Bazaar.Secrets;
using Npgsql;

namespace Bazaar.Server.Secrets.Postgres;

/// <summary>Persists scope data keys and sealed secret versions in <c>secret_scope_key</c> and <c>secret_record</c> (migration secrets/0001).</summary>
public sealed class PostgresSecretRecordRepository(NpgsqlDataSource dataSource) : ISecretRecordRepository
{
    public async Task<ScopeKeyRecord?> GetScopeKeyAsync(string scopeKey, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT scope_key, kek_id, wrapped_key, created_at, destroyed_at FROM secret_scope_key WHERE scope_key = $1", conn);
        cmd.Parameters.AddWithValue(scopeKey);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new ScopeKeyRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? ReadOnlyMemory<byte>.Empty : (byte[])reader.GetValue(2),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4));
    }

    public async Task<bool> TryInsertScopeKeyAsync(ScopeKeyRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO secret_scope_key (scope_key, kek_id, wrapped_key, created_at, destroyed_at) " +
            "VALUES ($1, $2, $3, $4, $5) ON CONFLICT (scope_key) DO NOTHING", conn);
        cmd.Parameters.AddWithValue(record.ScopeKey);
        cmd.Parameters.AddWithValue(record.KeyId);
        cmd.Parameters.AddWithValue(record.WrappedKey.ToArray());
        cmd.Parameters.AddWithValue(record.CreatedAt);
        cmd.Parameters.AddWithValue((object?)record.DestroyedAt ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task EraseScopeKeyAsync(string scopeKey, DateTimeOffset at, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "UPDATE secret_scope_key SET wrapped_key = NULL, destroyed_at = COALESCE(destroyed_at, $2) WHERE scope_key = $1", conn);
        cmd.Parameters.AddWithValue(scopeKey);
        cmd.Parameters.AddWithValue(at);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<SecretRecord?> GetSecretAsync(string scopeKey, string name, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT scope_key, name, version, nonce, ciphertext, tag, updated_at FROM secret_record WHERE scope_key = $1 AND name = $2", conn);
        cmd.Parameters.AddWithValue(scopeKey);
        cmd.Parameters.AddWithValue(name);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new SecretRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            (byte[])reader.GetValue(3),
            (byte[])reader.GetValue(4),
            (byte[])reader.GetValue(5),
            reader.GetFieldValue<DateTimeOffset>(6));
    }

    /// <summary>Writes a version only when it is newer than the stored one. Zero rows means another writer got there first.</summary>
    public async Task PutSecretAsync(SecretRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO secret_record (scope_key, name, version, nonce, ciphertext, tag, updated_at) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7) " +
            "ON CONFLICT (scope_key, name) DO UPDATE SET version = EXCLUDED.version, nonce = EXCLUDED.nonce, " +
            "ciphertext = EXCLUDED.ciphertext, tag = EXCLUDED.tag, updated_at = EXCLUDED.updated_at " +
            "WHERE secret_record.version < EXCLUDED.version", conn);
        cmd.Parameters.AddWithValue(record.ScopeKey);
        cmd.Parameters.AddWithValue(record.Name);
        cmd.Parameters.AddWithValue(record.Version);
        cmd.Parameters.AddWithValue(record.Nonce.ToArray());
        cmd.Parameters.AddWithValue(record.Ciphertext.ToArray());
        cmd.Parameters.AddWithValue(record.Tag.ToArray());
        cmd.Parameters.AddWithValue(record.UpdatedAt);
        if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException($"Secret '{record.Name}' in scope '{record.ScopeKey}' was written by another caller first; retry the put.");
        }
    }

    public async Task<bool> DeleteSecretAsync(string scopeKey, string name, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("DELETE FROM secret_record WHERE scope_key = $1 AND name = $2", conn);
        cmd.Parameters.AddWithValue(scopeKey);
        cmd.Parameters.AddWithValue(name);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task DeleteScopeSecretsAsync(string scopeKey, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("DELETE FROM secret_record WHERE scope_key = $1", conn);
        cmd.Parameters.AddWithValue(scopeKey);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
