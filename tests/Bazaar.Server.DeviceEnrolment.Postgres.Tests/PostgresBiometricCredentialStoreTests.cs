using Bazaar.Server.Biometric;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.DeviceEnrolment.Postgres.Tests;

public sealed class PostgresBiometricCredentialStoreTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private static async Task<(PostgresBiometricCredentialStore Store, Guid Human, Guid Device, byte[] Id, PgTestDatabase Db)> SeedAsync(CancellationToken ct)
    {
        var db = await EnrolmentDb.CreateAsync(ct);
        var (human, device) = await EnrolmentDb.InsertDeviceAsync(db.DataSource, "android", ct);
        var enrol = new PostgresEnrolmentStore(db.DataSource, new RecordingAudit());
        var nonce = EnrolmentDb.Nonce();
        var id = new byte[] { 5, 4, 3, 2, 1 };
        await enrol.SaveChallengeAsync(nonce, human, device, _now.AddMinutes(5), ct);
        var credential = new BiometricCredential(id, human, device.ToString("D"), [9, 8, 7], 10, new HashSet<BiometricFactor> { BiometricFactor.Fingerprint }, false);
        await enrol.CommitAsync(new EnrolmentCommit(human, device, DeviceClass.Android, nonce, null, [1], credential, _now), ct);
        return (new PostgresBiometricCredentialStore(db.DataSource), human, device, id, db);
    }

    [Fact]
    public async Task Get_returns_what_enrolment_wrote_and_null_for_an_unknown_id()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        var (store, human, device, id, db) = await SeedAsync(ct);
        await using var _ = db;

        var got = await store.GetAsync(id, ct);

        Assert.NotNull(got);
        Assert.Equal(human, got.HumanId);
        Assert.Equal(device.ToString("D"), got.DeviceId);
        Assert.Equal<byte[]>([9, 8, 7], got.PublicKeySpki);
        Assert.Equal(10u, got.SignCount);
        Assert.Equal([BiometricFactor.Fingerprint], got.AttestedFactors);
        Assert.False(got.Revoked);
        Assert.Null(await store.GetAsync([1, 1, 1], ct));
    }

    [Fact]
    public async Task The_counter_advances_only_from_the_expected_value()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        var (store, _, _, id, db) = await SeedAsync(ct);
        await using var _ = db;

        Assert.True(await store.AdvanceSignCountAsync(id, 10, 11, ct));
        Assert.False(await store.AdvanceSignCountAsync(id, 10, 12, ct));
        Assert.Equal(11u, (await store.GetAsync(id, ct))!.SignCount);
    }

    [Fact]
    public async Task The_counter_holds_values_above_the_signed_int_range()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        var (store, _, _, id, db) = await SeedAsync(ct);
        await using var _ = db;

        Assert.True(await store.AdvanceSignCountAsync(id, 10, uint.MaxValue, ct));
        Assert.Equal(uint.MaxValue, (await store.GetAsync(id, ct))!.SignCount);
    }

    [Fact]
    public async Task Revoking_marks_the_credential_and_stops_the_counter_moving()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        var (store, _, _, id, db) = await SeedAsync(ct);
        await using var _ = db;

        await store.RevokeAsync(id, ct);
        await store.RevokeAsync(id, ct);

        Assert.True((await store.GetAsync(id, ct))!.Revoked);
        Assert.False(await store.AdvanceSignCountAsync(id, 10, 11, ct));
    }
}
