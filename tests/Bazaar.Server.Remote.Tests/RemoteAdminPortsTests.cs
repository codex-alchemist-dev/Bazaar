using Bazaar.Client.Tui.Dashboard;
using Bazaar.PrivacyAudit;
using Bazaar.Server.Components.Remote;
using Bazaar.Server.WebAdmin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Bazaar.Server.Remote.Tests;

/// <summary>The identity read ports (docs 51 and 58) and the device revoker over the in-memory store.</summary>
public sealed class RemoteAdminPortsTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid _actor = Guid.Parse("0197a000-0000-7000-8000-00000000a001");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Guid> AddDeviceAsync(InMemoryRefreshCredentialStore store, Guid humanId, string label, DateTimeOffset at)
    {
        var id = Guid.CreateVersion7(at);
        await store.CreateDeviceAsync(
            new DeviceRecord(id, humanId, label, "linux", Revision: 1, RevokedAt: null),
            Guid.CreateVersion7(at),
            RefreshCredentialSecret.Hash(RefreshCredentialSecret.Generate()),
            at,
            at.AddDays(30),
            Ct);
        return id;
    }

    [Fact]
    public async Task Users_lists_people_and_marks_a_person_who_is_not_active_as_revoked()
    {
        var store = new InMemoryRefreshCredentialStore();
        var owner = Guid.CreateVersion7(_start);
        var gone = Guid.CreateVersion7(_start);
        var paused = Guid.CreateVersion7(_start);
        store.SetHuman(new HumanRecord(owner, "Owner", "owner", "active"));
        store.SetHuman(new HumanRecord(gone, "Zed", "coworker", "removed"));
        store.SetHuman(new HumanRecord(paused, "Pat", "coworker", "suspended"));

        var users = await new RemoteAdminUserDirectory(store).ListAsync(Ct);

        Assert.Equal(["Owner", "Pat", "Zed"], users.Select(u => u.DisplayName).ToArray());
        Assert.Equal([false, true, true], users.Select(u => u.Revoked).ToArray());
        Assert.All(users, u => Assert.False(u.IsAgent));
        Assert.Equal("owner", users[0].Role);
        Assert.Equal(owner, users[0].PrincipalId);
    }

    [Fact]
    public async Task Devices_lists_every_device_with_its_newest_credential_time_and_revocation()
    {
        var store = new InMemoryRefreshCredentialStore();
        var human = Guid.CreateVersion7(_start);
        var phone = await AddDeviceAsync(store, human, "phone", _start);
        var laptop = await AddDeviceAsync(store, human, "laptop", _start.AddHours(1));
        Assert.True(await store.RevokeDeviceAsync(laptop, _start.AddHours(2), Ct));

        // A rotation moves the phone's last activity forward.
        var credential = (await store.ListDevicesAsync(Ct)).Single(d => d.Device.Id == phone);
        Assert.Equal(_start, credential.LastActivity);

        var devices = await new RemoteAdminDeviceDirectory(store).ListAsync(Ct);

        Assert.Equal(["laptop", "phone"], devices.Select(d => d.Label).ToArray());
        Assert.Equal([true, false], devices.Select(d => d.Revoked).ToArray());
        Assert.Equal(_start.AddHours(1), devices[0].LastSeen);
        Assert.Equal(human, devices[1].PrincipalId);
    }

    [Fact]
    public async Task Listing_carries_no_secret()
    {
        var store = new InMemoryRefreshCredentialStore();
        var secret = RefreshCredentialSecret.Generate();
        var human = Guid.CreateVersion7(_start);
        await store.CreateDeviceAsync(
            new DeviceRecord(Guid.CreateVersion7(_start), human, "d", "linux", 1, null),
            Guid.CreateVersion7(_start),
            RefreshCredentialSecret.Hash(secret),
            _start,
            _start.AddDays(1),
            Ct);

        var json = System.Text.Json.JsonSerializer.Serialize(await new RemoteAdminDeviceDirectory(store).ListAsync(Ct));

        Assert.DoesNotContain(secret, json);
    }

    private static (RemoteOpsDeviceRevoker Revoker, InMemoryRefreshCredentialStore Store, FakeAuditLog Log) MakeRevoker()
    {
        var store = new InMemoryRefreshCredentialStore();
        var log = new FakeAuditLog();
        var time = new FakeTimeProvider(_start);
        var tokens = new TokenService(store, new FakeAccessTokenIssuer(), new PrivacyAuditRecorder(log, time), Options.Create(new RemoteTokenOptions()), time);
        return (new RemoteOpsDeviceRevoker(tokens), store, log);
    }

    [Fact]
    public async Task Revoker_revokes_through_the_token_service_and_audits_the_actor()
    {
        var (revoker, store, log) = MakeRevoker();
        var device = await AddDeviceAsync(store, Guid.CreateVersion7(_start), "phone", _start);

        var result = await revoker.RevokeDeviceAsync(_actor, device, Ct);

        Assert.Equal(new OpsWriteResult(true), result);
        Assert.NotNull((await store.FindDeviceAsync(device, Ct))?.RevokedAt);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(PrivacyAuditKinds.DeviceRevoked, entry.Kind);
        Assert.Contains(_actor.ToString("D"), entry.Actor);
    }

    [Fact]
    public async Task Revoker_is_idempotent_and_refuses_an_unknown_device_with_a_fixed_reason()
    {
        var (revoker, store, log) = MakeRevoker();
        var device = await AddDeviceAsync(store, Guid.CreateVersion7(_start), "phone", _start);
        await revoker.RevokeDeviceAsync(_actor, device, Ct);

        Assert.True((await revoker.RevokeDeviceAsync(_actor, device, Ct)).Done);
        Assert.Single(log.Entries);

        var unknown = await revoker.RevokeDeviceAsync(_actor, Guid.NewGuid(), Ct);
        Assert.False(unknown.Done);
        Assert.Equal("No such device.", unknown.Reason);
    }

    [Fact]
    public async Task Registration_resolves_the_ports_over_the_store_and_keeps_a_host_registration()
    {
        var services = new ServiceCollection();
        var store = new InMemoryRefreshCredentialStore();
        var mine = new RemoteAdminUserDirectory(store);
        services.AddSingleton<IRefreshCredentialStore>(store);
        services.AddSingleton<IAdminUserDirectory>(mine);
        services.AddBazaarRemoteAdminPorts();
        await using var provider = services.BuildServiceProvider();

        Assert.Same(mine, provider.GetRequiredService<IAdminUserDirectory>());
        Assert.NotNull(provider.GetRequiredService<IAdminDeviceDirectory>());
        Assert.Same(store, provider.GetRequiredService<IIdentityDirectoryStore>());
    }

    [Fact]
    public async Task Revoke_only_registration_revokes_but_never_issues_a_token()
    {
        var services = new ServiceCollection();
        var store = new InMemoryRefreshCredentialStore();
        var log = new FakeAuditLog();
        services.AddSingleton<IRefreshCredentialStore>(store);
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(_start));
        services.AddSingleton(new PrivacyAuditRecorder(log, new FakeTimeProvider(_start)));
        services.AddBazaarRemoteRevokeOnly();
        services.AddBazaarRemoteAdminPorts();
        await using var provider = services.BuildServiceProvider();
        var device = await AddDeviceAsync(store, Guid.CreateVersion7(_start), "phone", _start);

        Assert.True((await provider.GetRequiredService<IOpsDeviceRevoker>().RevokeDeviceAsync(_actor, device, Ct)).Done);
        Assert.Throws<AccessTokenIssuerUnavailableException>(() => provider.GetRequiredService<IAccessTokenIssuer>().Issue(device, _actor, _start));
    }
}

/// <summary>The same listings over the real tables on a real PostgreSQL. Skipped without <c>BAZAAR_TEST_PG</c>.</summary>
public sealed class PostgresIdentityDirectoryTests(RemoteAuthDb db) : IClassFixture<RemoteAuthDb>
{
    private static readonly DateTimeOffset _start = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task InsertHumanAsync(Guid id, string name, string role, string status)
    {
        await using var cmd = db.DataSource.CreateCommand("INSERT INTO human (id, display_name, role, status) VALUES ($1, $2, $3, $4)");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = name });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = role });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = status });
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    [Fact]
    public async Task Lists_humans_and_devices_with_last_activity_and_revocation_on_postgres()
    {
        Assert.SkipUnless(RemoteAuthDb.Enabled, "BAZAAR_TEST_PG is not set: no test PostgreSQL available.");
        var owner = Guid.CreateVersion7(_start);
        var gone = Guid.CreateVersion7(_start);
        await InsertHumanAsync(owner, "A directory owner", "owner", "active");
        await InsertHumanAsync(gone, "B directory removed", "coworker", "removed");
        var store = new PostgresRefreshCredentialStore(db.DataSource);
        var deviceId = Guid.CreateVersion7(_start);
        var first = RefreshCredentialSecret.Generate();
        await store.CreateDeviceAsync(
            new DeviceRecord(deviceId, owner, "A directory phone", "android", 1, null),
            Guid.CreateVersion7(_start),
            RefreshCredentialSecret.Hash(first),
            _start,
            _start.AddDays(30),
            Ct);
        var credential = await store.FindByHashAsync(RefreshCredentialSecret.Hash(first), Ct);
        Assert.NotNull(credential);
        var rotatedAt = _start.AddHours(3);
        Assert.True(await store.TryRotateAsync(deviceId, credential.Id, Guid.CreateVersion7(rotatedAt), RefreshCredentialSecret.Hash(RefreshCredentialSecret.Generate()), rotatedAt, rotatedAt.AddDays(30), Ct));

        var users = await new RemoteAdminUserDirectory(store).ListAsync(Ct);
        var devices = await new RemoteAdminDeviceDirectory(store).ListAsync(Ct);

        var ownerRow = Assert.Single(users, u => u.PrincipalId == owner);
        Assert.False(ownerRow.Revoked);
        Assert.Equal("owner", ownerRow.Role);
        Assert.True(Assert.Single(users, u => u.PrincipalId == gone).Revoked);
        var device = Assert.Single(devices, d => d.DeviceId == deviceId);
        Assert.Equal(rotatedAt, device.LastSeen);
        Assert.False(device.Revoked);

        Assert.True(await store.RevokeDeviceAsync(deviceId, rotatedAt.AddHours(1), Ct));
        Assert.True(Assert.Single(await new RemoteAdminDeviceDirectory(store).ListAsync(Ct), d => d.DeviceId == deviceId).Revoked);
    }
}
