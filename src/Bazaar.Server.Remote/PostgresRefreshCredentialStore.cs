using Npgsql;

namespace Bazaar.Server.Components.Remote;

/// <summary>
/// <see cref="IRefreshCredentialStore"/> over the shared <see cref="NpgsqlDataSource"/>, SQL-first (docs/implementation-plan.md).
/// Tables come from <c>remote/0001_pairing_auth.sql</c>. Hash lookups use the unique index on
/// <c>refresh_credential.hash</c>. Rotation is one transaction, and the partial unique index allows only one live row per device.
/// </summary>
public sealed class PostgresRefreshCredentialStore(NpgsqlDataSource dataSource) : IRefreshCredentialStore
{
    private const string _findCredentialSql = """
        SELECT rc.id, rc.device_id, rc.issued_at, rc.retired_at, rc.expires_at,
               (SELECT min(oldest.issued_at) FROM refresh_credential oldest WHERE oldest.device_id = rc.device_id)
        FROM refresh_credential rc
        WHERE rc.hash = $1
        """;

    private const string _findDeviceSql = "SELECT id, human_id, label, class, revision, revoked_at FROM device WHERE id = $1";

    private const string _insertDeviceSql =
        "INSERT INTO device (id, human_id, label, class, revision, created_at) VALUES ($1, $2, $3, $4, $5, $6)";

    private const string _insertCredentialSql =
        "INSERT INTO refresh_credential (id, device_id, hash, issued_at, expires_at) VALUES ($1, $2, $3, $4, $5)";

    // The retire statement also requires the device to be unrevoked, so a revocation that wins the race blocks rotation.
    private const string _retireSql = """
        UPDATE refresh_credential SET retired_at = $3
        WHERE id = $1 AND device_id = $2 AND retired_at IS NULL
          AND EXISTS (SELECT 1 FROM device d WHERE d.id = $2 AND d.revoked_at IS NULL)
        """;

    private const string _revokeSql = "UPDATE device SET revoked_at = $2, revision = revision + 1 WHERE id = $1 AND revoked_at IS NULL";

    public async Task<RefreshCredentialRecord?> FindByHashAsync(byte[] hash, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hash);
        await using var cmd = dataSource.CreateCommand(_findCredentialSql);
        cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = hash });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;
        return new RefreshCredentialRecord(
            Id: reader.GetGuid(0),
            DeviceId: reader.GetGuid(1),
            IssuedAt: reader.GetFieldValue<DateTimeOffset>(2),
            RetiredAt: reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
            ExpiresAt: reader.GetFieldValue<DateTimeOffset>(4),
            ChainStartedAt: reader.GetFieldValue<DateTimeOffset>(5));
    }

    public async Task<DeviceRecord?> FindDeviceAsync(Guid deviceId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(_findDeviceSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = deviceId });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;
        return new DeviceRecord(
            Id: reader.GetGuid(0),
            HumanId: reader.GetGuid(1),
            Label: reader.GetString(2),
            Class: reader.GetString(3),
            Revision: reader.GetInt32(4),
            RevokedAt: reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5));
    }

    public async Task CreateDeviceAsync(
        DeviceRecord device,
        Guid credentialId,
        byte[] hash,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(hash);
        if (device.RevokedAt is not null)
            throw new ArgumentException("A new device cannot be revoked.", nameof(device));

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var insertDevice = new NpgsqlCommand(_insertDeviceSql, conn, tx))
        {
            insertDevice.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = device.Id });
            insertDevice.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = device.HumanId });
            insertDevice.Parameters.Add(new NpgsqlParameter<string> { TypedValue = device.Label });
            insertDevice.Parameters.Add(new NpgsqlParameter<string> { TypedValue = device.Class });
            insertDevice.Parameters.Add(new NpgsqlParameter<int> { TypedValue = device.Revision });
            insertDevice.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = issuedAt });
            await insertDevice.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await InsertCredentialAsync(conn, tx, credentialId, device.Id, hash, issuedAt, expiresAt, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> TryRotateAsync(
        Guid deviceId,
        Guid currentCredentialId,
        Guid nextCredentialId,
        byte[] nextHash,
        DateTimeOffset now,
        DateTimeOffset nextExpiresAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(nextHash);
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        int retired;
        await using (var retire = new NpgsqlCommand(_retireSql, conn, tx))
        {
            retire.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = currentCredentialId });
            retire.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = deviceId });
            retire.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
            retired = await retire.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Nothing was retired: already rotated, or the device is revoked. The transaction is disposed without a commit.
        if (retired == 0)
            return false;

        await InsertCredentialAsync(conn, tx, nextCredentialId, deviceId, nextHash, now, nextExpiresAt, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> RevokeDeviceAsync(Guid deviceId, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(_revokeSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = deviceId });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    private static async Task InsertCredentialAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid credentialId,
        Guid deviceId,
        byte[] hash,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        CancellationToken ct)
    {
        await using var insert = new NpgsqlCommand(_insertCredentialSql, conn, tx);
        insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = credentialId });
        insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = deviceId });
        insert.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = hash });
        insert.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = issuedAt });
        insert.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = expiresAt });
        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
