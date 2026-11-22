using System.Security.Cryptography;
using Bazaar.Server.Biometric;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.DeviceEnrolment.Postgres.Tests;

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
    }
}
