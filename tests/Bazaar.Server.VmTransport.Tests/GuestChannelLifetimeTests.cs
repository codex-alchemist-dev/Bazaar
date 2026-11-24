using Bazaar.Server.VmMounts.Abstractions;

namespace Bazaar.Server.VmTransport.Tests;

public class GuestChannelLifetimeTests
{
    [Fact]
    public async Task A_real_channel_reports_open_then_not_open_after_dispose_and_leaves_the_directory()
    {
        await using var rig = await AgentRig.StartAsync();
        var agent = Guid.NewGuid();
        var vm = Guid.NewGuid();
        var registry = new GuestChannelRegistry();
        var lookup = new FakeVmLookup();
        lookup.Vms[agent] = [new AgentVmInfo(vm, GuestOs.Linux, "bazaar-vm1", false)];
        var directory = new GuestChannelDirectory(registry, lookup);
        registry.Register(vm, agent.ToString("D"), "bazaar-vm1", GuestOs.Linux, rig.Channel);
        Assert.True(((IGuestChannelLifetime)rig.Channel).IsOpen);
        Assert.NotNull(await directory.TryGetByVmAsync(vm, TestContext.Current.CancellationToken));

        await rig.Channel.DisposeAsync();

        Assert.False(rig.Channel.IsOpen);
        Assert.Null(await directory.TryGetByVmAsync(vm, TestContext.Current.CancellationToken));
    }
}
