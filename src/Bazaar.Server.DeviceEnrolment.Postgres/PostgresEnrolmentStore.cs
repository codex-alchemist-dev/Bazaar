using System.Text;
using System.Text.Json;
using Bazaar.Server.Biometric;
using Bazaar.Server.Security.Audit;
using Npgsql;
using Bazaar.Server.Biometric;
using Npgsql;
using NpgsqlTypes;

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
/// Durable <see cref="IEnrolmentStore"/> (doc 32 section 5b) over <c>enrolment_challenge</c>, <c>enrolment_code</c> and
/// <c>biometric_credential</c> (device_enrolment 0001) and the <c>approval_key</c> table that <c>PostgresOwnerKeyRegistry</c> reads.
/// This is the only writer of that table's enrolment rows.
/// </summary>
/// <remarks>
/// Reads never spend anything. <see cref="CommitAsync"/> runs in one transaction: it spends the nonce and the console code, inserts the
/// approval key and the credential, and appends the audit row. Any refusal rolls the whole transaction back, so a failed attempt
/// spends neither the nonce nor the code. Expiry is judged against the caller's clock (<c>now</c>, <see cref="EnrolmentCommit.At"/>),
/// never the database's, so the ceremony and the store agree. A device's commits are serialised by a transaction advisory lock, so two
/// racing commits for one device cannot both pass the "one live pair" check.
/// Hostile input never throws: a credential id already held elsewhere answers <see cref="EnrolmentCommitResult.AlreadyEnrolled"/>.
/// </remarks>
public sealed class PostgresEnrolmentStore(NpgsqlDataSource dataSource) : IEnrolmentStore
{
    private const string _saveChallenge =
        "INSERT INTO enrolment_challenge (nonce, human_id, device_id, expires_at) VALUES ($1, $2, $3, $4)";
    private const string _saveCode =
        "INSERT INTO enrolment_code (code_hash, human_id, expires_at) VALUES ($1, $2, $3) ON CONFLICT (code_hash) DO NOTHING";
    private const string _challengeLive =
        "SELECT EXISTS (SELECT 1 FROM enrolment_challenge " +
        "WHERE nonce = $1 AND human_id = $2 AND device_id = $3 AND spent_at IS NULL AND expires_at > $4)";
    private const string _codeLive =
        "SELECT EXISTS (SELECT 1 FROM enrolment_code WHERE code_hash = $1 AND human_id = $2 AND spent_at IS NULL AND expires_at > $3)";
    private const string _deviceMatches =
        "SELECT EXISTS (SELECT 1 FROM device WHERE id = $1 AND human_id = $2 AND class = $3 AND revoked_at IS NULL)";

    private const string _lockDevice = "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))";
    private const string _lockAudit = "SELECT pg_advisory_xact_lock($1)";
    private const string _spendChallenge =
        "UPDATE enrolment_challenge SET spent_at = $5 " +
        "WHERE nonce = $1 AND human_id = $2 AND device_id = $3 AND spent_at IS NULL AND expires_at > $4";
    private const string _spendCode =
        "UPDATE enrolment_code SET spent_at = $4 WHERE code_hash = $1 AND human_id = $2 AND spent_at IS NULL AND expires_at > $3";
    private const string _alreadyEnrolled =
        "SELECT EXISTS (SELECT 1 FROM approval_key WHERE device_id = $1 AND revoked_at IS NULL) " +
        "OR EXISTS (SELECT 1 FROM biometric_credential WHERE device_id = $1 AND revoked_at IS NULL)";
    private const string _insertApprovalKey =
        "INSERT INTO approval_key (id, human_id, device_id, public_key, created_at) VALUES ($1, $2, $3, $4, $5)";
    private const string _insertCredential =
        "INSERT INTO biometric_credential (credential_id, human_id, device_id, public_key, sign_count, attested_factors, created_at) " +
        "VALUES ($1, $2, $3, $4, $5, $6, $7)";
    private const string _auditHead = "SELECT seq, hash FROM audit_log ORDER BY seq DESC LIMIT 1";
    private const string _auditCanonical = "SELECT CAST($1 AS jsonb)::text";
    private const string _auditInsert =
        "INSERT INTO audit_log (seq, ts, actor, kind, payload, prev_hash, hash) VALUES ($1, $2, $3, $4, CAST($5 AS jsonb), $6, $7)";

    private const string _uniqueViolation = "23505";
    private const string _foreignKeyViolation = "23503";

    /// <inheritdoc />
    public async Task SaveChallengeAsync(byte[] nonce, Guid humanId, Guid deviceId, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(nonce);
        await using var cmd = dataSource.CreateCommand(_saveChallenge);
        cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = nonce });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = humanId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = deviceId });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = expiresAt.ToUniversalTime() });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveCodeAsync(byte[] codeHash, Guid humanId, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(codeHash);
        await using var cmd = dataSource.CreateCommand(_saveCode);
        cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = codeHash });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = humanId });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = expiresAt.ToUniversalTime() });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> IsChallengeLiveAsync(byte[] nonce, Guid humanId, Guid deviceId, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(nonce);
        await using var cmd = dataSource.CreateCommand(_challengeLive);
        cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = nonce });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = humanId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = deviceId });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now.ToUniversalTime() });
        return (bool)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<bool> IsCodeLiveAsync(byte[] codeHash, Guid humanId, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(codeHash);
        await using var cmd = dataSource.CreateCommand(_codeLive);
        cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = codeHash });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = humanId });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now.ToUniversalTime() });
        return (bool)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<bool> DeviceMatchesAsync(Guid deviceId, Guid humanId, DeviceClass deviceClass, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(_deviceMatches);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = deviceId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = humanId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = DeviceClassNames.Name(deviceClass) });
        return (bool)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task<EnrolmentCommitResult> CommitAsync(EnrolmentCommit commit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(commit.Nonce);
        ArgumentNullException.ThrowIfNull(commit.ApprovalPublicKeySpki);
        ArgumentNullException.ThrowIfNull(commit.Credential);

        var at = commit.At.ToUniversalTime();
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await RunAsync(conn, tx, commit, at, ct).ConfigureAwait(false);
            if (result == EnrolmentCommitResult.Committed)
            {
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
            }

            return result;
        }
        catch (PostgresException ex) when (ex.SqlState is _uniqueViolation or _foreignKeyViolation)
        {
            // The failed statement aborted the transaction, so nothing was written. A unique violation is a credential id or a live
            // pair that exists already; a foreign key violation is a device or human that does not exist.
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return ex.SqlState == _uniqueViolation ? EnrolmentCommitResult.AlreadyEnrolled : EnrolmentCommitResult.DeviceUnknown;
        }
    }

    private static async Task<EnrolmentCommitResult> RunAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, EnrolmentCommit c, DateTimeOffset at, CancellationToken ct)
    {
        await using (var lockDevice = new NpgsqlCommand(_lockDevice, conn, tx))
        {
            lockDevice.Parameters.Add(new NpgsqlParameter<string> { TypedValue = c.DeviceId.ToString("D") });
            await lockDevice.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var device = new NpgsqlCommand(_deviceMatches, conn, tx))
        {
            device.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = c.DeviceId });
            device.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = c.HumanId });
            device.Parameters.Add(new NpgsqlParameter<string> { TypedValue = DeviceClassNames.Name(c.Class) });
            if (!(bool)(await device.ExecuteScalarAsync(ct).ConfigureAwait(false))!)
            {
                return EnrolmentCommitResult.DeviceUnknown;
            }
        }

        await using (var spend = new NpgsqlCommand(_spendChallenge, conn, tx))
        {
            spend.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = c.Nonce });
            spend.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = c.HumanId });
            spend.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = c.DeviceId });
            spend.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = at });
            spend.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = at });
            if (await spend.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                return EnrolmentCommitResult.ChallengeSpent;
            }
        }

        if (c.ConsoleCodeHash is not null)
        {
            await using var spend = new NpgsqlCommand(_spendCode, conn, tx);
            spend.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = c.ConsoleCodeHash });
            spend.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = c.HumanId });
            spend.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = at });
            spend.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = at });
            if (await spend.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                return EnrolmentCommitResult.CodeSpent;
            }
        }

        await using (var exists = new NpgsqlCommand(_alreadyEnrolled, conn, tx))
        {
            exists.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = c.DeviceId });
            if ((bool)(await exists.ExecuteScalarAsync(ct).ConfigureAwait(false))!)
            {
                return EnrolmentCommitResult.AlreadyEnrolled;
            }
        }

        var enrolmentId = Guid.CreateVersion7();
        await using (var key = new NpgsqlCommand(_insertApprovalKey, conn, tx))
        {
            key.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = enrolmentId });
            key.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = c.HumanId });
            key.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = c.DeviceId });
            key.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = c.ApprovalPublicKeySpki });
            key.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = at });
            await key.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var credential = new NpgsqlCommand(_insertCredential, conn, tx))
        {
            var cred = c.Credential;
            credential.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = cred.CredentialId });
            credential.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = c.HumanId });
            credential.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = c.DeviceId });
            credential.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = cred.PublicKeySpki });
            credential.Parameters.Add(new NpgsqlParameter<long> { TypedValue = cred.SignCount });
            credential.Parameters.Add(new NpgsqlParameter<string[]>
            {
                TypedValue = [.. cred.AttestedFactors.Select(BiometricNames.Name).Order(StringComparer.Ordinal)],
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
            });
            credential.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = at });
            await credential.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await AppendAuditAsync(conn, tx, c, enrolmentId, at, ct).ConfigureAwait(false);
        return EnrolmentCommitResult.Committed;
    }

    private static async Task AppendAuditAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, EnrolmentCommit c, Guid enrolmentId, DateTimeOffset at, CancellationToken ct)
    {
        await using (var lockAudit = new NpgsqlCommand(_lockAudit, conn, tx))
        {
            lockAudit.Parameters.Add(new NpgsqlParameter<long> { TypedValue = EnrolmentAuditRow.AppendLockKey });
            await lockAudit.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        long headSeq = 0;
        var headHash = EnrolmentAuditRow.GenesisHash;
        await using (var head = new NpgsqlCommand(_auditHead, conn, tx))
        await using (var reader = await head.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                headSeq = reader.GetInt64(0);
                headHash = reader.GetFieldValue<byte[]>(1);
            }
        }

        // Let Postgres normalise the JSON text, so a row read back hashes to the value it was written with.
        string canonical;
        await using (var norm = new NpgsqlCommand(_auditCanonical, conn, tx))
        {
            norm.Parameters.Add(new NpgsqlParameter<string> { TypedValue = EnrolmentAuditRow.Payload(enrolmentId, c.DeviceId) });
            canonical = (string)(await norm.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }

        var actor = c.HumanId.ToString("D");
        var ts = EnrolmentAuditRow.Truncate(at);
        var seq = headSeq + 1;
        var hash = EnrolmentAuditRow.ComputeHash(seq, ts, actor, EnrolmentAuditRow.Kind, canonical, headHash);
        await using var insert = new NpgsqlCommand(_auditInsert, conn, tx);
        insert.Parameters.Add(new NpgsqlParameter<long> { TypedValue = seq });
        insert.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = ts });
        insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = actor });
        insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = EnrolmentAuditRow.Kind });
        insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = canonical });
        insert.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = headHash });
        insert.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = hash });
        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
