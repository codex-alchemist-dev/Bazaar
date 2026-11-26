using System.Security.Cryptography;
using Bazaar.Server.Biometric;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.DeviceEnrolment.Postgres.Tests;

using Bazaar.Approvals.Db;
using Bazaar.Server.Biometric;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.DeviceEnrolment.Postgres.Tests;

/// <summary>Runs the store against a real PostgreSQL. Skipped unless <c>BAZAAR_TEST_PG</c> is set.</summary>
public sealed class PostgresEnrolmentStoreTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private static EnrolmentCommit Commit(Guid human, Guid device, byte[] nonce, byte[]? codeHash = null, byte[]? credentialId = null) =>
        new(human, device, DeviceClass.Windows, nonce, codeHash, [1, 2, 3],
            new BiometricCredential(credentialId ?? RandomNumberGenerator.GetBytes(16), human, device.ToString("D"), [9, 9], 4,
                new HashSet<BiometricFactor> { BiometricFactor.Fingerprint, BiometricFactor.FaceDepth }, Revoked: false),
            _now);

    [Fact]
    public async Task A_commit_writes_the_key_the_credential_and_the_audit_row_and_spends_the_challenge_and_code()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await EnrolmentDb.CreateAsync(ct);
        var (human, device) = await EnrolmentDb.InsertDeviceAsync(db.DataSource, "windows", ct);
        var audit = new RecordingAudit();
        var store = new PostgresEnrolmentStore(db.DataSource, audit);
        var nonce = EnrolmentDb.Nonce();
        var code = SHA256.HashData([7]);
        await store.SaveChallengeAsync(nonce, human, device, _now.AddMinutes(5), ct);
        await store.SaveCodeAsync(code, human, _now.AddMinutes(5), ct);

        Assert.True(await store.IsChallengeLiveAsync(nonce, human, device, _now, ct));
        Assert.True(await store.IsCodeLiveAsync(code, human, _now, ct));
        Assert.True(await store.DeviceMatchesAsync(device, human, DeviceClass.Windows, ct));

        var result = await store.CommitAsync(Commit(human, device, nonce, code), ct);

        Assert.Equal(EnrolmentCommitResult.Committed, result);
        Assert.Equal(1, await EnrolmentDb.Count(db.DataSource, "SELECT count(*) FROM approval_key WHERE device_id = $1 AND revoked_at IS NULL", ct, device));
        Assert.Equal(1, await EnrolmentDb.Count(db.DataSource, "SELECT count(*) FROM biometric_credential WHERE device_id = $1", ct, device));
        Assert.False(await store.IsChallengeLiveAsync(nonce, human, device, _now, ct));
        Assert.False(await store.IsCodeLiveAsync(code, human, _now, ct));
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("device.enrolled", entry.Kind);
        Assert.Equal(human.ToString("D"), entry.Actor);
        Assert.DoesNotContain("010203", entry.Payload);
    }

    [Fact]
    public async Task A_challenge_is_bound_to_its_human_and_device_and_expires()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await EnrolmentDb.CreateAsync(ct);
        var (human, device) = await EnrolmentDb.InsertDeviceAsync(db.DataSource, "windows", ct);
        var store = new PostgresEnrolmentStore(db.DataSource, new RecordingAudit());
        var nonce = EnrolmentDb.Nonce();
        await store.SaveChallengeAsync(nonce, human, device, _now.AddMinutes(5), ct);

        Assert.False(await store.IsChallengeLiveAsync(nonce, Guid.NewGuid(), device, _now, ct));
        Assert.False(await store.IsChallengeLiveAsync(nonce, human, Guid.NewGuid(), _now, ct));
        Assert.False(await store.IsChallengeLiveAsync(nonce, human, device, _now.AddMinutes(5), ct));
        Assert.Equal(EnrolmentCommitResult.ChallengeSpent, await store.CommitAsync(Commit(human, device, nonce) with { At = _now.AddMinutes(6) }, ct));
    }

    [Fact]
    public async Task A_spent_challenge_cannot_be_used_twice()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await EnrolmentDb.CreateAsync(ct);
        var (human, device) = await EnrolmentDb.InsertDeviceAsync(db.DataSource, "windows", ct);
        var store = new PostgresEnrolmentStore(db.DataSource, new RecordingAudit());
        var nonce = EnrolmentDb.Nonce();
        await store.SaveChallengeAsync(nonce, human, device, _now.AddMinutes(5), ct);
        Assert.Equal(EnrolmentCommitResult.Committed, await store.CommitAsync(Commit(human, device, nonce), ct));

        Assert.Equal(EnrolmentCommitResult.ChallengeSpent, await store.CommitAsync(Commit(human, device, nonce), ct));
    }

    [Fact]
    public async Task A_second_enrolment_of_one_device_is_refused_and_spends_nothing()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await EnrolmentDb.CreateAsync(ct);
        var (human, device) = await EnrolmentDb.InsertDeviceAsync(db.DataSource, "windows", ct);
        var store = new PostgresEnrolmentStore(db.DataSource, new RecordingAudit());
        var first = EnrolmentDb.Nonce();
        var second = EnrolmentDb.Nonce();
        await store.SaveChallengeAsync(first, human, device, _now.AddMinutes(5), ct);
        await store.SaveChallengeAsync(second, human, device, _now.AddMinutes(5), ct);
        await store.CommitAsync(Commit(human, device, first), ct);

        var result = await store.CommitAsync(Commit(human, device, second), ct);

        Assert.Equal(EnrolmentCommitResult.AlreadyEnrolled, result);
        Assert.True(await store.IsChallengeLiveAsync(second, human, device, _now, ct));
        Assert.Equal(1, await EnrolmentDb.Count(db.DataSource, "SELECT count(*) FROM approval_key WHERE device_id = $1", ct, device));
    }

    [Fact]
    public async Task A_spent_code_refuses_the_commit_and_keeps_the_challenge()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await EnrolmentDb.CreateAsync(ct);
        var (human, device) = await EnrolmentDb.InsertDeviceAsync(db.DataSource, "windows", ct);
        var store = new PostgresEnrolmentStore(db.DataSource, new RecordingAudit());
        var nonce = EnrolmentDb.Nonce();
        var code = SHA256.HashData([8]);
        await store.SaveChallengeAsync(nonce, human, device, _now.AddMinutes(5), ct);
        await store.SaveCodeAsync(code, human, _now.AddMinutes(5), ct);
        await EnrolmentDb.Exec(db.DataSource, "UPDATE enrolment_code SET spent_at = $1", ct, _now);

        var result = await store.CommitAsync(Commit(human, device, nonce, code), ct);

        Assert.Equal(EnrolmentCommitResult.CodeSpent, result);
        Assert.True(await store.IsChallengeLiveAsync(nonce, human, device, _now, ct));
        Assert.Equal(0, await EnrolmentDb.Count(db.DataSource, "SELECT count(*) FROM approval_key", ct));
    }

    [Fact]
    public async Task An_unknown_wrong_class_or_revoked_device_is_refused()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await EnrolmentDb.CreateAsync(ct);
        var (human, device) = await EnrolmentDb.InsertDeviceAsync(db.DataSource, "linux", ct);
        var store = new PostgresEnrolmentStore(db.DataSource, new RecordingAudit());
        var nonce = EnrolmentDb.Nonce();
        await store.SaveChallengeAsync(nonce, human, device, _now.AddMinutes(5), ct);

        Assert.Equal(EnrolmentCommitResult.DeviceUnknown, await store.CommitAsync(Commit(human, device, nonce), ct)); // Windows commit, Linux row
        Assert.Equal(EnrolmentCommitResult.DeviceUnknown, await store.CommitAsync(Commit(human, Guid.NewGuid(), nonce), ct));

        await EnrolmentDb.Exec(db.DataSource, "UPDATE device SET revoked_at = now()", ct);
        Assert.False(await store.DeviceMatchesAsync(device, human, DeviceClass.Linux, ct));
    }

    [Fact]
    public async Task A_failing_audit_row_rolls_the_whole_commit_back()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await EnrolmentDb.CreateAsync(ct);
        var (human, device) = await EnrolmentDb.InsertDeviceAsync(db.DataSource, "windows", ct);
        var audit = new RecordingAudit { Throw = true };
        var store = new PostgresEnrolmentStore(db.DataSource, audit);
        var nonce = EnrolmentDb.Nonce();
        await store.SaveChallengeAsync(nonce, human, device, _now.AddMinutes(5), ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitAsync(Commit(human, device, nonce), ct));

        Assert.True(await store.IsChallengeLiveAsync(nonce, human, device, _now, ct));
        Assert.Equal(0, await EnrolmentDb.Count(db.DataSource, "SELECT count(*) FROM approval_key", ct));
        Assert.Equal(0, await EnrolmentDb.Count(db.DataSource, "SELECT count(*) FROM biometric_credential", ct));
    }

    [Fact]
    public async Task Two_racing_commits_for_one_device_write_one_pair()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await EnrolmentDb.CreateAsync(ct);
        var (human, device) = await EnrolmentDb.InsertDeviceAsync(db.DataSource, "windows", ct);
        var store = new PostgresEnrolmentStore(db.DataSource, new RecordingAudit());
        var a = EnrolmentDb.Nonce();
        var b = EnrolmentDb.Nonce();
        await store.SaveChallengeAsync(a, human, device, _now.AddMinutes(5), ct);
        await store.SaveChallengeAsync(b, human, device, _now.AddMinutes(5), ct);

        var results = await Task.WhenAll(store.CommitAsync(Commit(human, device, a), ct), store.CommitAsync(Commit(human, device, b), ct));

        Assert.Single(results, r => r == EnrolmentCommitResult.Committed);
        Assert.Single(results, r => r == EnrolmentCommitResult.AlreadyEnrolled);
        Assert.Equal(1, await EnrolmentDb.Count(db.DataSource, "SELECT count(*) FROM approval_key", ct));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record World(PgTestDatabase Db, PostgresEnrolmentStore Store, Guid Human, Guid Device) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private static async Task<World> NewWorldAsync(DeviceClass deviceClass = DeviceClass.Windows)
    {
        Assert.SkipUnless(EnrolmentDatabase.Available, EnrolmentDatabase.SkipReason);
        var db = await EnrolmentDatabase.CreateAsync(Ct);
        var human = Guid.NewGuid();
        await ExecAsync(db, "INSERT INTO human (id, display_name, role, status) VALUES ($1, 'Owner', 'owner', 'active')", human);
        var device = await AddDeviceAsync(db, human, deviceClass);
        return new World(db, new PostgresEnrolmentStore(db.DataSource), human, device);
    }

    private static async Task<Guid> AddDeviceAsync(PgTestDatabase db, Guid human, DeviceClass deviceClass = DeviceClass.Windows)
    {
        var device = Guid.NewGuid();
        await ExecAsync(db, "INSERT INTO device (id, human_id, label, class) VALUES ($1, $2, 'laptop', $3)", device, human, DeviceClassNames.Name(deviceClass));
        return device;
    }

    private static async Task ExecAsync(PgTestDatabase db, string sql, params object[] args)
    {
        await using var cmd = db.DataSource.CreateCommand(sql);
        foreach (var a in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        }

        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<T> ScalarAsync<T>(PgTestDatabase db, string sql, params object[] args)
    {
        await using var cmd = db.DataSource.CreateCommand(sql);
        foreach (var a in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        }

        return (T)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private static byte[] Nonce() => RandomNumberGenerator.GetBytes(32);

    private static byte[] Hash() => RandomNumberGenerator.GetBytes(32);

    private static EnrolmentCommit Commit(World w, byte[] nonce, byte[]? codeHash, Guid? device = null, byte[]? credentialId = null, DateTimeOffset? at = null)
    {
        var d = device ?? w.Device;
        var credential = new BiometricCredential(
            credentialId ?? RandomNumberGenerator.GetBytes(32), w.Human, d.ToString("D"), RandomNumberGenerator.GetBytes(91), 7,
            new HashSet<BiometricFactor> { BiometricFactor.Fingerprint, BiometricFactor.HardwareKey }, Revoked: false);
        return new EnrolmentCommit(w.Human, d, DeviceClass.Windows, nonce, codeHash, RandomNumberGenerator.GetBytes(91), credential, at ?? _now);
    }

    private static async Task<(byte[] Nonce, byte[] CodeHash)> ArmAsync(World w, Guid? device = null)
    {
        var nonce = Nonce();
        var code = Hash();
        await w.Store.SaveChallengeAsync(nonce, w.Human, device ?? w.Device, _now.AddMinutes(5), Ct);
        await w.Store.SaveCodeAsync(code, w.Human, _now.AddMinutes(15), Ct);
        return (nonce, code);
    }

    [Fact]
    public async Task A_saved_challenge_is_live_only_for_its_human_and_device_before_expiry()
    {
        await using var w = await NewWorldAsync();
        var nonce = Nonce();
        await w.Store.SaveChallengeAsync(nonce, w.Human, w.Device, _now.AddMinutes(5), Ct);

        Assert.True(await w.Store.IsChallengeLiveAsync(nonce, w.Human, w.Device, _now, Ct));
        Assert.False(await w.Store.IsChallengeLiveAsync(nonce, Guid.NewGuid(), w.Device, _now, Ct));
        Assert.False(await w.Store.IsChallengeLiveAsync(nonce, w.Human, Guid.NewGuid(), _now, Ct));
        Assert.False(await w.Store.IsChallengeLiveAsync(Nonce(), w.Human, w.Device, _now, Ct));
        Assert.False(await w.Store.IsChallengeLiveAsync(nonce, w.Human, w.Device, _now.AddMinutes(5), Ct));
        Assert.False(await w.Store.IsChallengeLiveAsync(nonce, w.Human, w.Device, _now.AddHours(1), Ct));
    }

    [Fact]
    public async Task A_saved_code_is_live_only_for_its_human_before_expiry_and_only_its_hash_is_stored()
    {
        await using var w = await NewWorldAsync();
        var hash = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes("ABCDEFGH23"));
        await w.Store.SaveCodeAsync(hash, w.Human, _now.AddMinutes(15), Ct);

        Assert.True(await w.Store.IsCodeLiveAsync(hash, w.Human, _now, Ct));
        Assert.False(await w.Store.IsCodeLiveAsync(hash, Guid.NewGuid(), _now, Ct));
        Assert.False(await w.Store.IsCodeLiveAsync(Hash(), w.Human, _now, Ct));
        Assert.False(await w.Store.IsCodeLiveAsync(hash, w.Human, _now.AddMinutes(15), Ct));
        Assert.Equal(1L, await ScalarAsync<long>(w.Db, "SELECT count(*) FROM enrolment_code WHERE code_hash = $1", hash));
        Assert.Equal(32, await ScalarAsync<int>(w.Db, "SELECT octet_length(code_hash) FROM enrolment_code"));
    }

    [Fact]
    public async Task The_code_table_refuses_a_row_that_is_not_a_32_byte_hash()
    {
        await using var w = await NewWorldAsync();

        await Assert.ThrowsAsync<PostgresException>(() => w.Store.SaveCodeAsync(System.Text.Encoding.ASCII.GetBytes("ABCDEFGH23"), w.Human, _now.AddMinutes(15), Ct));
    }

    [Fact]
    public async Task Saving_the_same_code_hash_twice_is_harmless()
    {
        await using var w = await NewWorldAsync();
        var hash = Hash();

        await w.Store.SaveCodeAsync(hash, w.Human, _now.AddMinutes(15), Ct);
        await w.Store.SaveCodeAsync(hash, w.Human, _now.AddMinutes(30), Ct);

        Assert.Equal(1L, await ScalarAsync<long>(w.Db, "SELECT count(*) FROM enrolment_code"));
    }

    [Fact]
    public async Task DeviceMatches_needs_the_human_the_class_and_a_live_device()
    {
        await using var w = await NewWorldAsync(DeviceClass.Android);

        Assert.True(await w.Store.DeviceMatchesAsync(w.Device, w.Human, DeviceClass.Android, Ct));
        Assert.False(await w.Store.DeviceMatchesAsync(w.Device, w.Human, DeviceClass.Windows, Ct));
        Assert.False(await w.Store.DeviceMatchesAsync(w.Device, Guid.NewGuid(), DeviceClass.Android, Ct));
        Assert.False(await w.Store.DeviceMatchesAsync(Guid.NewGuid(), w.Human, DeviceClass.Android, Ct));

        await ExecAsync(w.Db, "UPDATE device SET revoked_at = now() WHERE id = $1", w.Device);
        Assert.False(await w.Store.DeviceMatchesAsync(w.Device, w.Human, DeviceClass.Android, Ct));
    }

    [Fact]
    public async Task Commit_writes_the_key_the_credential_and_the_audit_row_and_spends_nonce_and_code()
    {
        await using var w = await NewWorldAsync();
        var (nonce, code) = await ArmAsync(w);
        var commit = Commit(w, nonce, code);

        var result = await w.Store.CommitAsync(commit, Ct);

        Assert.Equal(EnrolmentCommitResult.Committed, result);
        Assert.False(await w.Store.IsChallengeLiveAsync(nonce, w.Human, w.Device, _now, Ct));
        Assert.False(await w.Store.IsCodeLiveAsync(code, w.Human, _now, Ct));
        Assert.Equal(commit.ApprovalPublicKeySpki, await ScalarAsync<byte[]>(w.Db, "SELECT public_key FROM approval_key WHERE device_id = $1", w.Device));
        Assert.Equal("ecdsa-p256", await ScalarAsync<string>(w.Db, "SELECT alg FROM approval_key WHERE device_id = $1", w.Device));
        Assert.Equal(_now.UtcDateTime, await ScalarAsync<DateTime>(w.Db, "SELECT created_at FROM approval_key WHERE device_id = $1", w.Device));
        Assert.Equal(w.Human, await ScalarAsync<Guid>(w.Db, "SELECT human_id FROM approval_key WHERE device_id = $1", w.Device));

        var c = commit.Credential;
        Assert.Equal(c.PublicKeySpki, await ScalarAsync<byte[]>(w.Db, "SELECT public_key FROM biometric_credential WHERE credential_id = $1", c.CredentialId));
        Assert.Equal(7L, await ScalarAsync<long>(w.Db, "SELECT sign_count FROM biometric_credential WHERE credential_id = $1", c.CredentialId));
        Assert.Equal(["fingerprint", "hardware-key"], await ScalarAsync<string[]>(w.Db, "SELECT attested_factors FROM biometric_credential WHERE credential_id = $1", c.CredentialId));
        Assert.Equal(w.Device, await ScalarAsync<Guid>(w.Db, "SELECT device_id FROM biometric_credential WHERE credential_id = $1", c.CredentialId));
    }

    [Fact]
    public async Task The_committed_key_is_what_the_owner_key_registry_reads()
    {
        await using var w = await NewWorldAsync();
        var (nonce, code) = await ArmAsync(w);
        var commit = Commit(w, nonce, code);
        Assert.Equal(EnrolmentCommitResult.Committed, await w.Store.CommitAsync(commit, Ct));

        var found = await new PostgresOwnerKeyRegistry(w.Db.DataSource).FindAsync(w.Device.ToString("D"), Ct);

        Assert.NotNull(found);
        Assert.Equal(commit.ApprovalPublicKeySpki, found.PublicKeySpki);
        Assert.Equal(w.Human.ToString("D"), found.OwnerId);
        Assert.False(found.Revoked);
    }

    [Fact]
    public async Task Commit_appends_one_audit_row_that_extends_the_chain_without_key_material()
    {
        await using var w = await NewWorldAsync();
        var first = await ArmAsync(w);
        Assert.Equal(EnrolmentCommitResult.Committed, await w.Store.CommitAsync(Commit(w, first.Nonce, first.CodeHash), Ct));
        var secondDevice = await AddDeviceAsync(w.Db, w.Human);
        var second = await ArmAsync(w, secondDevice);
        var commit = Commit(w, second.Nonce, null, secondDevice, at: _now.AddMinutes(1));
        Assert.Equal(EnrolmentCommitResult.Committed, await w.Store.CommitAsync(commit, Ct));

        Assert.Equal(2L, await ScalarAsync<long>(w.Db, "SELECT count(*) FROM audit_log"));
        await using var cmd = w.Db.DataSource.CreateCommand("SELECT seq, ts, actor, kind, payload::text, prev_hash, hash FROM audit_log ORDER BY seq");
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        var prev = EnrolmentAuditRow.GenesisHash;
        var expectedSeq = 1L;
        while (await reader.ReadAsync(Ct))
        {
            Assert.Equal(expectedSeq++, reader.GetInt64(0));
            Assert.Equal(w.Human.ToString("D"), reader.GetString(2));
            Assert.Equal("approval_key.enrolled", reader.GetString(3));
            var payload = reader.GetString(4);
            Assert.Equal(prev, reader.GetFieldValue<byte[]>(5));
            var hash = EnrolmentAuditRow.ComputeHash(reader.GetInt64(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetString(2), reader.GetString(3), payload, prev);
            Assert.Equal(hash, reader.GetFieldValue<byte[]>(6));
            Assert.DoesNotContain(Convert.ToBase64String(commit.ApprovalPublicKeySpki), payload);
            prev = hash;
        }

        Assert.Equal(3L, expectedSeq);
    }

    [Fact]
    public async Task A_spent_nonce_cannot_enrol_again_even_for_a_new_device_pair()
    {
        await using var w = await NewWorldAsync();
        var (nonce, code) = await ArmAsync(w);
        Assert.Equal(EnrolmentCommitResult.Committed, await w.Store.CommitAsync(Commit(w, nonce, code), Ct));

        var replay = await w.Store.CommitAsync(Commit(w, nonce, null), Ct);

        Assert.Equal(EnrolmentCommitResult.ChallengeSpent, replay);
        Assert.Equal(1L, await ScalarAsync<long>(w.Db, "SELECT count(*) FROM approval_key"));
    }

    [Fact]
    public async Task An_unknown_or_expired_or_foreign_nonce_is_reported_as_spent_and_writes_nothing()
    {
        await using var w = await NewWorldAsync();
        var (nonce, code) = await ArmAsync(w);
        var otherDevice = await AddDeviceAsync(w.Db, w.Human);

        Assert.Equal(EnrolmentCommitResult.ChallengeSpent, await w.Store.CommitAsync(Commit(w, Nonce(), code), Ct));
        Assert.Equal(EnrolmentCommitResult.ChallengeSpent, await w.Store.CommitAsync(Commit(w, nonce, code, at: _now.AddMinutes(5)), Ct));
        Assert.Equal(EnrolmentCommitResult.ChallengeSpent, await w.Store.CommitAsync(Commit(w, nonce, code, otherDevice), Ct));

        Assert.Equal(0L, await ScalarAsync<long>(w.Db, "SELECT count(*) FROM approval_key"));
        Assert.Equal(0L, await ScalarAsync<long>(w.Db, "SELECT count(*) FROM biometric_credential"));
        Assert.Equal(0L, await ScalarAsync<long>(w.Db, "SELECT count(*) FROM audit_log"));
        Assert.True(await w.Store.IsCodeLiveAsync(code, w.Human, _now, Ct));
    }

    [Fact]
    public async Task A_spent_code_refuses_the_commit_and_leaves_the_nonce_unspent()
    {
        await using var w = await NewWorldAsync();
        var (nonce, code) = await ArmAsync(w);
        var otherDevice = await AddDeviceAsync(w.Db, w.Human);
        var other = await ArmAsync(w, otherDevice);
        await w.Store.SaveCodeAsync(code, w.Human, _now.AddMinutes(15), Ct);
        Assert.Equal(EnrolmentCommitResult.Committed, await w.Store.CommitAsync(Commit(w, other.Nonce, code, otherDevice), Ct));

        var result = await w.Store.CommitAsync(Commit(w, nonce, code), Ct);

        Assert.Equal(EnrolmentCommitResult.CodeSpent, result);
        Assert.True(await w.Store.IsChallengeLiveAsync(nonce, w.Human, w.Device, _now, Ct));
    }

    [Fact]
    public async Task A_code_issued_to_another_human_or_expired_is_refused()
    {
        await using var w = await NewWorldAsync();
        var nonce = Nonce();
        await w.Store.SaveChallengeAsync(nonce, w.Human, w.Device, _now.AddMinutes(5), Ct);
        var expired = Hash();
        await w.Store.SaveCodeAsync(expired, w.Human, _now.AddMinutes(1), Ct);
        var strangers = Hash();
        var stranger = Guid.NewGuid();
        await ExecAsync(w.Db, "INSERT INTO human (id, display_name, role, status) VALUES ($1, 'Cow', 'coworker', 'active')", stranger);
        await w.Store.SaveCodeAsync(strangers, stranger, _now.AddMinutes(15), Ct);

        Assert.Equal(EnrolmentCommitResult.CodeSpent, await w.Store.CommitAsync(Commit(w, nonce, expired, at: _now.AddMinutes(2)), Ct));
        Assert.Equal(EnrolmentCommitResult.CodeSpent, await w.Store.CommitAsync(Commit(w, nonce, strangers), Ct));
        Assert.True(await w.Store.IsChallengeLiveAsync(nonce, w.Human, w.Device, _now, Ct));
    }

    [Fact]
    public async Task A_second_enrolment_for_a_device_is_refused_and_spends_neither_nonce_nor_code()
    {
        await using var w = await NewWorldAsync();
        var first = await ArmAsync(w);
        Assert.Equal(EnrolmentCommitResult.Committed, await w.Store.CommitAsync(Commit(w, first.Nonce, first.CodeHash), Ct));
        var second = await ArmAsync(w);

        var result = await w.Store.CommitAsync(Commit(w, second.Nonce, second.CodeHash), Ct);

        Assert.Equal(EnrolmentCommitResult.AlreadyEnrolled, result);
        Assert.True(await w.Store.IsChallengeLiveAsync(second.Nonce, w.Human, w.Device, _now, Ct));
        Assert.True(await w.Store.IsCodeLiveAsync(second.CodeHash, w.Human, _now, Ct));
        Assert.Equal(1L, await ScalarAsync<long>(w.Db, "SELECT count(*) FROM approval_key"));
        Assert.Equal(1L, await ScalarAsync<long>(w.Db, "SELECT count(*) FROM audit_log"));
    }

    [Fact]
    public async Task After_both_keys_are_revoked_the_device_can_enrol_again()
    {
        await using var w = await NewWorldAsync();
        var first = await ArmAsync(w);
        Assert.Equal(EnrolmentCommitResult.Committed, await w.Store.CommitAsync(Commit(w, first.Nonce, first.CodeHash), Ct));
        await ExecAsync(w.Db, "UPDATE approval_key SET revoked_at = now() WHERE device_id = $1", w.Device);
        await ExecAsync(w.Db, "UPDATE biometric_credential SET revoked_at = now() WHERE device_id = $1", w.Device);
        var second = await ArmAsync(w);

        var result = await w.Store.CommitAsync(Commit(w, second.Nonce, second.CodeHash), Ct);

        Assert.Equal(EnrolmentCommitResult.Committed, result);
        Assert.Equal(2L, await ScalarAsync<long>(w.Db, "SELECT count(*) FROM approval_key WHERE device_id = $1", w.Device));
    }

    [Fact]
    public async Task A_credential_id_held_by_another_device_is_refused_without_throwing_and_rolls_everything_back()
    {
        await using var w = await NewWorldAsync();
        var otherDevice = await AddDeviceAsync(w.Db, w.Human);
        var first = await ArmAsync(w, otherDevice);
        var shared = RandomNumberGenerator.GetBytes(32);
        Assert.Equal(EnrolmentCommitResult.Committed, await w.Store.CommitAsync(Commit(w, first.Nonce, first.CodeHash, otherDevice, shared), Ct));
        var second = await ArmAsync(w);

        var result = await w.Store.CommitAsync(Commit(w, second.Nonce, second.CodeHash, credentialId: shared), Ct);

        Assert.Equal(EnrolmentCommitResult.AlreadyEnrolled, result);
        Assert.True(await w.Store.IsChallengeLiveAsync(second.Nonce, w.Human, w.Device, _now, Ct));
        Assert.True(await w.Store.IsCodeLiveAsync(second.CodeHash, w.Human, _now, Ct));
        Assert.Equal(1L, await ScalarAsync<long>(w.Db, "SELECT count(*) FROM approval_key"));
        Assert.Equal(1L, await ScalarAsync<long>(w.Db, "SELECT count(*) FROM audit_log"));
    }

    [Fact]
    public async Task A_device_that_does_not_match_is_reported_unknown_and_writes_nothing()
    {
        await using var w = await NewWorldAsync();
        var ghost = Guid.NewGuid();
        var nonce = Nonce();
        var commit = Commit(w, nonce, null, ghost);

        Assert.Equal(EnrolmentCommitResult.DeviceUnknown, await w.Store.CommitAsync(commit, Ct));
        Assert.Equal(EnrolmentCommitResult.DeviceUnknown, await w.Store.CommitAsync(commit with { Class = DeviceClass.Linux, DeviceId = w.Device }, Ct));
        await ExecAsync(w.Db, "UPDATE device SET revoked_at = now() WHERE id = $1", w.Device);
        Assert.Equal(EnrolmentCommitResult.DeviceUnknown, await w.Store.CommitAsync(commit with { DeviceId = w.Device }, Ct));
        Assert.Equal(0L, await ScalarAsync<long>(w.Db, "SELECT count(*) FROM approval_key"));
    }

    [Fact]
    public async Task Racing_commits_for_one_device_let_exactly_one_through()
    {
        await using var w = await NewWorldAsync();
        var attempts = new List<EnrolmentCommit>();
        for (var i = 0; i < 8; i++)
        {
            var (nonce, code) = await ArmAsync(w);
            attempts.Add(Commit(w, nonce, code));
        }

        var results = await Task.WhenAll(attempts.Select(a => Task.Run(() => w.Store.CommitAsync(a, Ct), Ct)));

        Assert.Equal(1, results.Count(r => r == EnrolmentCommitResult.Committed));
        Assert.All(results.Where(r => r != EnrolmentCommitResult.Committed), r => Assert.Equal(EnrolmentCommitResult.AlreadyEnrolled, r));
        Assert.Equal(1L, await ScalarAsync<long>(w.Db, "SELECT count(*) FROM approval_key"));
        Assert.Equal(1L, await ScalarAsync<long>(w.Db, "SELECT count(*) FROM biometric_credential"));
        Assert.Equal(1L, await ScalarAsync<long>(w.Db, "SELECT count(*) FROM audit_log"));
    }

    [Fact]
    public async Task Racing_commits_with_one_nonce_let_exactly_one_through()
    {
        await using var w = await NewWorldAsync();
        var (nonce, _) = await ArmAsync(w);
        var attempts = new List<EnrolmentCommit>();
        for (var i = 0; i < 6; i++)
        {
            attempts.Add(Commit(w, nonce, null));
        }

        var results = await Task.WhenAll(attempts.Select(a => Task.Run(() => w.Store.CommitAsync(a, Ct), Ct)));

        Assert.Equal(1, results.Count(r => r == EnrolmentCommitResult.Committed));
        Assert.Equal(1L, await ScalarAsync<long>(w.Db, "SELECT count(*) FROM approval_key"));
    }

    [Fact]
    public async Task An_unattested_credential_stores_no_factors()
    {
        await using var w = await NewWorldAsync();
        var (nonce, code) = await ArmAsync(w);
        var commit = Commit(w, nonce, code);
        commit = commit with { Credential = commit.Credential with { AttestedFactors = new HashSet<BiometricFactor>() } };

        Assert.Equal(EnrolmentCommitResult.Committed, await w.Store.CommitAsync(commit, Ct));

        Assert.Empty(await ScalarAsync<string[]>(w.Db, "SELECT attested_factors FROM biometric_credential"));
    }
}
