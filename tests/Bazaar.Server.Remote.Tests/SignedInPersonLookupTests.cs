using Bazaar.Server.Components.Remote;

namespace Bazaar.Server.Remote.Tests;

/// <summary>The device and human checks behind <c>GET /auth/whoami</c> (doc 32 section 5).</summary>
public sealed class SignedInPersonLookupTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(SignedInPersonLookup Lookup, InMemoryRefreshCredentialStore Store, Guid DeviceId, Guid HumanId)> SeedAsync(string status = "active")
    {
        var store = new InMemoryRefreshCredentialStore();
        var humanId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        store.SetHuman(new HumanRecord(humanId, "Ada", "owner", status));
        await store.CreateDeviceAsync(new DeviceRecord(deviceId, humanId, "pc", "windows", 1, null), Guid.NewGuid(), [1, 2, 3], _now, _now.AddDays(30), Ct);
        return (new SignedInPersonLookup(store), store, deviceId, humanId);
    }

    [Fact]
    public async Task Active_human_and_live_device_resolve()
    {
        var (lookup, _, deviceId, humanId) = await SeedAsync();
        Assert.Equal(new SignedInPerson(humanId, "Ada", "owner", deviceId), await lookup.FindAsync(deviceId, humanId, Ct));
    }

    [Fact]
    public async Task Revoked_device_does_not_resolve()
    {
        var (lookup, store, deviceId, humanId) = await SeedAsync();
        await store.RevokeDeviceAsync(deviceId, _now, Ct);
        Assert.Null(await lookup.FindAsync(deviceId, humanId, Ct));
    }

    [Theory]
    [InlineData("suspended")]
    [InlineData("removed")]
    public async Task Inactive_human_does_not_resolve(string status)
    {
        var (lookup, _, deviceId, humanId) = await SeedAsync(status);
        Assert.Null(await lookup.FindAsync(deviceId, humanId, Ct));
    }

    [Fact]
    public async Task Device_of_another_human_or_unknown_device_does_not_resolve()
    {
        var (lookup, _, deviceId, humanId) = await SeedAsync();
        Assert.Null(await lookup.FindAsync(deviceId, Guid.NewGuid(), Ct));
        Assert.Null(await lookup.FindAsync(Guid.NewGuid(), humanId, Ct));
    }
}
