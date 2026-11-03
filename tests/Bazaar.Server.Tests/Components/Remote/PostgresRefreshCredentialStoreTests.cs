using Bazaar.Server.Components.PrivacyAudit;
using Bazaar.Server.Components.Remote;
using Bazaar.Server.Tests.Components.PrivacyAudit;
using Bazaar.Server.Wire.Connection;
using Bazaar.Testing.Postgres;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Bazaar.Server.Tests.Components.Remote;

/// <summary>
/// A cloned database with the remote component's pairing and auth tables (<c>components/remote/0001_pairing_auth.sql</c>).
/// The component SQL is not applied by the migration runner yet, so the test applies the embedded script itself.
/// Runs only when <c>BAZAAR_TEST_PG</c> is set; otherwise it does nothing and the tests skip.
/// </summary>
public sealed class RemoteAuthDb : PgFixture, IAsyncLifetime
{
    private const string _schemaResourceSuffix = "0001_pairing_auth.sql";

    /// <summary>True when a test PostgreSQL was provided through <c>BAZAAR_TEST_PG</c>.</summary>
    public static bool Enabled => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PgSource.EnvConnectionString));

    public override async ValueTask InitializeAsync()
    {
        if (!Enabled)
            return;
        await base.InitializeAsync();
        await using var cmd = DataSource.CreateCommand(ReadSchema());
        await cmd.ExecuteNonQueryAsync();
    }

    private static string ReadSchema()
    {
        var assembly = typeof(PostgresRefreshCredentialStore).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(_schemaResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("The remote auth schema resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>Rotation, reuse detection and revocation through the real tables on a real PostgreSQL.</summary>
public sealed class PostgresRefreshCredentialStoreTests(RemoteAuthDb db) : IClassFixture<RemoteAuthDb>
{
    private static readonly DateTimeOffset _start = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> InsertCoworkerAsync()
    {
        var humanId = Guid.CreateVersion7(_start);
        await using var insert = db.DataSource.CreateCommand(
            "INSERT INTO human (id, display_name, role, status) VALUES ($1, 'Test coworker', 'coworker', 'active')");
        insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = humanId });
        await insert.ExecuteNonQueryAsync(Ct);
        return humanId;
    }

    [Fact]
    public async Task Rotation_then_reuse_revokes_the_device_and_audits_it_on_postgres()
    {
        Assert.SkipUnless(RemoteAuthDb.Enabled, "BAZAAR_TEST_PG is not set: no test PostgreSQL available.");
        var humanId = await InsertCoworkerAsync();
        var deviceId = Guid.CreateVersion7(_start);
        var secret = RefreshCredentialSecret.Generate();
        var store = new PostgresRefreshCredentialStore(db.DataSource);
        await store.CreateDeviceAsync(
            new DeviceRecord(deviceId, humanId, "test phone", "android", Revision: 1, RevokedAt: null),
            Guid.CreateVersion7(_start),
            RefreshCredentialSecret.Hash(secret),
            _start,
            _start.AddDays(30),
            Ct);

        var time = new FakeTimeProvider(_start);
        var log = new FakeAuditLog();
        var service = new TokenService(
            store,
            new FixedAccessTokenIssuer(),
            new PrivacyAuditRecorder(log, time),
            Options.Create(new RemoteTokenOptions()),
            time);
        var device = new DeviceInfo(deviceId.ToString("D"), "android", "test-build");

        var pair = await service.RefreshAsync(secret, device, Ct);
        var reuse = await Assert.ThrowsAsync<TokenRefreshException>(() => service.RefreshAsync(secret, device, Ct));

        Assert.Equal(TokenRefreshFailure.ReuseDetected, reuse.Failure);
        var stored = await store.FindDeviceAsync(deviceId, Ct);
        Assert.NotNull(stored?.RevokedAt);
        Assert.Equal(2, stored.Revision);
        Assert.Equal(
            new[] { PrivacyAuditKinds.DeviceRevoked, PrivacyAuditKinds.TokenReuseDetected },
            log.Entries.Select(e => e.Kind).ToArray());

        // The credential from the rotation is still stored as live, but the revoked device is refused.
        var live = await store.FindByHashAsync(RefreshCredentialSecret.Hash(pair.RefreshToken), Ct);
        Assert.NotNull(live);
        Assert.Null(live.RetiredAt);
        var refused = await Assert.ThrowsAsync<TokenRefreshException>(() => service.RefreshAsync(pair.RefreshToken, device, Ct));
        Assert.Equal(TokenRefreshFailure.DeviceRevoked, refused.Failure);
    }

    [Fact]
    public async Task Revoke_sets_revoked_at_once_and_bumps_the_revision_on_postgres()
    {
        Assert.SkipUnless(RemoteAuthDb.Enabled, "BAZAAR_TEST_PG is not set: no test PostgreSQL available.");
        var humanId = await InsertCoworkerAsync();
        var deviceId = Guid.CreateVersion7(_start);
        var store = new PostgresRefreshCredentialStore(db.DataSource);
        await store.CreateDeviceAsync(
            new DeviceRecord(deviceId, humanId, "test laptop", "linux", Revision: 1, RevokedAt: null),
            Guid.CreateVersion7(_start),
            RefreshCredentialSecret.Hash(RefreshCredentialSecret.Generate()),
            _start,
            _start.AddDays(30),
            Ct);

        Assert.True(await store.RevokeDeviceAsync(deviceId, _start, Ct));
        Assert.False(await store.RevokeDeviceAsync(deviceId, _start.AddHours(1), Ct));

        var stored = await store.FindDeviceAsync(deviceId, Ct);
        Assert.Equal(_start, stored!.RevokedAt);
        Assert.Equal(2, stored.Revision);
    }

    /// <summary>The access token value is not under test here; the rotation tests only need one.</summary>
    private sealed class FixedAccessTokenIssuer : IAccessTokenIssuer
    {
        public string Issue(Guid deviceId, Guid humanId, DateTimeOffset expiresAt) => "access-token-under-test";
    }
}
