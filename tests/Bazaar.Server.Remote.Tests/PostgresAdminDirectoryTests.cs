using Bazaar.Server.Components.Remote;
using Bazaar.Server.WebAdmin;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Remote.Tests;

public sealed class PostgresAdminDirectoryTests(RemoteAuthDb db) : IClassFixture<RemoteAuthDb>
{
    private static readonly DateTimeOffset _t = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("active", false)]
    [InlineData("suspended", true)]
    [InlineData("removed", true)]
    public void A_user_is_revoked_unless_active(string status, bool revoked)
    {
        var user = PostgresAdminDirectory.MapUser(Guid.NewGuid(), "Ada", "coworker", status);

        Assert.Equal(revoked, user.Revoked);
        Assert.False(user.IsAgent);
        Assert.Equal("coworker", user.Role);
    }

    [Fact]
    public void A_device_falls_back_to_pairing_time_and_flags_revocation()
    {
        var fresh = PostgresAdminDirectory.MapDevice(Guid.NewGuid(), Guid.NewGuid(), "phone", _t, null, null);
        var seen = PostgresAdminDirectory.MapDevice(Guid.NewGuid(), Guid.NewGuid(), "pc", _t, _t.AddDays(2), _t.AddDays(1));

        Assert.Equal(_t, fresh.LastSeen);
        Assert.False(fresh.Revoked);
        Assert.Equal(_t.AddDays(1), seen.LastSeen);
        Assert.True(seen.Revoked);
    }

    [Fact]
    public void Registration_exposes_both_ports_from_one_instance()
    {
        var services = new ServiceCollection();
        services.AddSingleton(NpgsqlDataSource.Create("Host=localhost"));
        services.AddBazaarRemoteTokens();
        using var sp = services.BuildServiceProvider();

        Assert.Same(sp.GetRequiredService<IAdminUserDirectory>(), sp.GetRequiredService<IAdminDeviceDirectory>());
    }

    [Fact]
    public async Task Lists_humans_and_devices_from_postgres()
    {
        Assert.SkipUnless(RemoteAuthDb.Enabled, "BAZAAR_TEST_PG is not set: no test PostgreSQL available.");
        var humanId = Guid.CreateVersion7(_t);
        var deviceId = Guid.CreateVersion7(_t);
        await using (var h = db.DataSource.CreateCommand("INSERT INTO human (id, display_name, role, status) VALUES ($1, 'Dir user', 'coworker', 'suspended')"))
        {
            h.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = humanId });
            await h.ExecuteNonQueryAsync(Ct);
        }
        await using (var d = db.DataSource.CreateCommand("INSERT INTO device (id, human_id, label, class, created_at, revoked_at) VALUES ($1, $2, 'dir phone', 'android', $3, $3)"))
        {
            d.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = deviceId });
            d.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = humanId });
            d.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = _t });
            await d.ExecuteNonQueryAsync(Ct);
        }
        var directory = new PostgresAdminDirectory(db.DataSource);

        var users = await ((IAdminUserDirectory)directory).ListAsync(Ct);
        var devices = await ((IAdminDeviceDirectory)directory).ListAsync(Ct);

        Assert.Contains(users, u => u.PrincipalId == humanId && u.Revoked && u.DisplayName == "Dir user");
        Assert.Contains(devices, x => x.DeviceId == deviceId && x.PrincipalId == humanId && x.Revoked && x.LastSeen == _t);
    }
}
