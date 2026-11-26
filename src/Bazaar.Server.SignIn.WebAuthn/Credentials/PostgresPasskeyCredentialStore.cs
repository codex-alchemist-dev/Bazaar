using Npgsql;

namespace Bazaar.Server.SignIn.WebAuthn.Credentials;

/// <summary>
/// <see cref="IPasskeyCredentialStore"/> over the shared <see cref="NpgsqlDataSource"/>, SQL-first. The table comes from
/// <c>sign_in_webauthn/0001_webauthn_credential.sql</c>. The sign-count advance is a single guarded UPDATE, so two concurrent
/// assertions with the same counter cannot both win.
/// </summary>
public sealed class PostgresPasskeyCredentialStore(NpgsqlDataSource dataSource) : IPasskeyCredentialStore
{
    private const string _columns = "id, principal_id, device_id, credential_id, public_key, sign_count, label, created_at, revoked_at";

    private const string _findSql = "SELECT " + _columns + " FROM webauthn_credential WHERE credential_id = $1 AND revoked_at IS NULL";

    private const string _insertSql = """
        INSERT INTO webauthn_credential (id, principal_id, device_id, credential_id, public_key, sign_count, label, created_at)
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
        ON CONFLICT DO NOTHING
        """;

    private const string _advanceSql = """
        UPDATE webauthn_credential SET sign_count = $3, last_used_at = $4
        WHERE id = $1 AND sign_count = $2 AND revoked_at IS NULL
        """;

    private const string _listSql = "SELECT credential_id FROM webauthn_credential WHERE principal_id = $1 AND revoked_at IS NULL";

    private const string _countSql = "SELECT count(*) FROM webauthn_credential WHERE principal_id = $1 AND revoked_at IS NULL";

    private const string _revokeSql = "UPDATE webauthn_credential SET revoked_at = $2 WHERE id = $1 AND revoked_at IS NULL";

    public async Task<StoredPasskeyCredential?> FindByCredentialIdAsync(byte[] credentialId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentialId);
        await using var cmd = dataSource.CreateCommand(_findSql);
        cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = credentialId });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new StoredPasskeyCredential(
            Id: reader.GetGuid(0),
            PrincipalId: reader.GetGuid(1),
            DeviceId: reader.GetGuid(2),
            CredentialId: reader.GetFieldValue<byte[]>(3),
            PublicKey: reader.GetFieldValue<byte[]>(4),
            SignCount: reader.GetInt64(5),
            Label: reader.GetString(6),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(7),
            RevokedAt: reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8));
    }

    public async Task<bool> AddAsync(StoredPasskeyCredential credential, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        await using var cmd = dataSource.CreateCommand(_insertSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = credential.Id });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = credential.PrincipalId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = credential.DeviceId });
        cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = credential.CredentialId });
        cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = credential.PublicKey });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = credential.SignCount });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = credential.Label });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = credential.CreatedAt });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task<bool> TryAdvanceSignCountAsync(Guid id, long expectedCount, long newCount, DateTimeOffset usedAt, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(_advanceSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = expectedCount });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = newCount });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = usedAt });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task<IReadOnlyList<byte[]>> ListCredentialIdsAsync(Guid principalId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(_listSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = principalId });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var ids = new List<byte[]>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ids.Add(reader.GetFieldValue<byte[]>(0));
        }

        return ids;
    }

    public async Task<int> CountAsync(Guid principalId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(_countSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = principalId });
        return (int)(long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    public async Task<bool> RevokeAsync(Guid id, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(_revokeSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }
}
