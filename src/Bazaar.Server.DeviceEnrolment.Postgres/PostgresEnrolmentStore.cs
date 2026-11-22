using System.Text;
using System.Text.Json;
using Bazaar.Server.Biometric;
using Bazaar.Server.Security.Audit;
using Npgsql;

namespace Bazaar.Server.DeviceEnrolment.Postgres;

/// <summary>
/// <see cref="IEnrolmentStore"/> over the <c>device_enrolment</c> tables (migration device_enrolment/0001) and the <c>device</c> and
/// <c>approval_key</c> tables of the remote namespace. <see cref="CommitAsync"/> runs in one transaction under a per-device advisory
/// lock, so two racing enrolments of one device cannot both commit; any refusal rolls the transaction back, so nothing is spent.
/// </summary>
public sealed class PostgresEnrolmentStore(NpgsqlDataSource dataSource, ITransactionalAuditLog audit) : IEnrolmentStore
{
    public async Task SaveChallengeAsync(byte[] nonce, Guid humanId, Guid deviceId, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "INSERT INTO enrolment_challenge (nonce, human_id, device_id, expires_at) VALUES ($1, $2, $3, $4)");
        cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = nonce });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = humanId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = deviceId });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = expiresAt });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveCodeAsync(byte[] codeHash, Guid humanId, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "INSERT INTO enrolment_code (code_hash, human_id, expires_at) VALUES ($1, $2, $3)");
        cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = codeHash });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = humanId });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = expiresAt });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> IsChallengeLiveAsync(byte[] nonce, Guid humanId, Guid deviceId, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT 1 FROM enrolment_challenge WHERE nonce = $1 AND human_id = $2 AND device_id = $3 AND spent_at IS NULL AND expires_at > $4");
        cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = nonce });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = humanId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = deviceId });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    public async Task<bool> IsCodeLiveAsync(byte[] codeHash, Guid humanId, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT 1 FROM enrolment_code WHERE code_hash = $1 AND human_id = $2 AND spent_at IS NULL AND expires_at > $3");
        cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = codeHash });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = humanId });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    public async Task<bool> DeviceMatchesAsync(Guid deviceId, Guid humanId, DeviceClass deviceClass, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await DeviceMatchesAsync(conn, null, deviceId, humanId, deviceClass, ct).ConfigureAwait(false);
    }

    public async Task<EnrolmentCommitResult> CommitAsync(EnrolmentCommit commit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", conn, tx))
        {
            lockCmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = "device_enrol:" + commit.DeviceId.ToString("D") });
            await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (!await DeviceMatchesAsync(conn, tx, commit.DeviceId, commit.HumanId, commit.Class, ct).ConfigureAwait(false))
            return EnrolmentCommitResult.DeviceUnknown;

        if (!await SpendAsync(conn, tx, "UPDATE enrolment_challenge SET spent_at = $4 WHERE nonce = $1 AND human_id = $2 AND device_id = $3 AND spent_at IS NULL AND expires_at > $4",
                commit.Nonce, commit.HumanId, commit.DeviceId, commit.At, ct).ConfigureAwait(false))
            return EnrolmentCommitResult.ChallengeSpent;

        if (commit.ConsoleCodeHash is { } codeHash
            && !await SpendAsync(conn, tx, "UPDATE enrolment_code SET spent_at = $3 WHERE code_hash = $1 AND human_id = $2 AND spent_at IS NULL AND expires_at > $3",
                codeHash, commit.HumanId, null, commit.At, ct).ConfigureAwait(false))
            return EnrolmentCommitResult.CodeSpent;

        if (await HasLivePairAsync(conn, tx, commit.DeviceId, ct).ConfigureAwait(false))
            return EnrolmentCommitResult.AlreadyEnrolled;

        await InsertApprovalKeyAsync(conn, tx, commit, ct).ConfigureAwait(false);
        await InsertCredentialAsync(conn, tx, commit, ct).ConfigureAwait(false);
        await audit.AppendInTransactionAsync(conn, tx, Entry(commit), ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return EnrolmentCommitResult.Committed;
    }

    private static async Task<bool> DeviceMatchesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid deviceId, Guid humanId, DeviceClass deviceClass, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM device WHERE id = $1 AND human_id = $2 AND class = $3 AND revoked_at IS NULL", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = deviceId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = humanId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = ClassName(deviceClass) });
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    /// <summary>Runs a spend statement whose parameters are ($1 key, $2 human, [$3 device], then the time as the last one).</summary>
    private static async Task<bool> SpendAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, byte[] key, Guid humanId, Guid? deviceId, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = key });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = humanId });
        if (deviceId is { } d)
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = d });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = at });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    private static async Task<bool> HasLivePairAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid deviceId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM approval_key WHERE device_id = $1 AND revoked_at IS NULL) " +
            "OR EXISTS (SELECT 1 FROM biometric_credential WHERE device_id = $1 AND revoked_at IS NULL)", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = deviceId });
        return (bool)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    private static async Task InsertApprovalKeyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, EnrolmentCommit c, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO approval_key (id, human_id, device_id, public_key, created_at) VALUES ($1, $2, $3, $4, $5)", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = Guid.CreateVersion7() });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = c.HumanId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = c.DeviceId });
        cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = c.ApprovalPublicKeySpki });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = c.At });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task InsertCredentialAsync(NpgsqlConnection conn, NpgsqlTransaction tx, EnrolmentCommit c, CancellationToken ct)
    {
        var cred = c.Credential;
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO biometric_credential (credential_id, human_id, device_id, public_key, sign_count, attested_factors, created_at) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7)", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = cred.CredentialId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = c.HumanId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = c.DeviceId });
        cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = cred.PublicKeySpki });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = cred.SignCount });
        cmd.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = [.. cred.AttestedFactors.Select(BiometricNames.Name).Order(StringComparer.Ordinal)] });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = c.At });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static AuditEntry Entry(EnrolmentCommit c)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("attested_factors", string.Join(',', c.Credential.AttestedFactors.Select(BiometricNames.Name).Order(StringComparer.Ordinal)));
            w.WriteString("class", ClassName(c.Class));
            w.WriteString("console_code", c.ConsoleCodeHash is null ? "no" : "yes");
            w.WriteString("credential", Convert.ToHexString(c.Credential.CredentialId));
            w.WriteString("device", c.DeviceId.ToString("D"));
            w.WriteEndObject();
        }

        return new AuditEntry(c.At, c.HumanId.ToString("D"), "device.enrolled", Encoding.UTF8.GetString(ms.ToArray()));
    }

    internal static string ClassName(DeviceClass deviceClass) => deviceClass.ToString().ToLowerInvariant();
}
