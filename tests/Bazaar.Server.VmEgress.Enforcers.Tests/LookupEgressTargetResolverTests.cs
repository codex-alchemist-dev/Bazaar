using Bazaar.Server.VmEgress;
using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.VmTransport;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.VmEgress.Enforcers.Tests;

public sealed class LookupEgressTargetResolverTests
{
    private static readonly Guid _agent = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private sealed class Lookup(params AgentVmInfo[] vms) : IAgentVmLookup
    {
        public ValueTask<IReadOnlyList<AgentVmInfo>> ListAsync(Guid agentId, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<AgentVmInfo>>(agentId == _agent ? vms : []);
    }

    // Fails the test if the enforcer reaches the host: the resolver result alone must stop it.
    private sealed class NoRoot : IRootCommandRunner
    {
        public Task<HostCommandResult> RunAsync(RootCommand command, CancellationToken ct = default) => throw new InvalidOperationException("The host must not be reached.");
    }

    private static AgentVmInfo Vm(string? name, GuestOs os = GuestOs.Linux, bool retired = false, string? iface = null) =>
        new(Guid.NewGuid(), os, name, retired, iface);

    private static Task<EgressTarget> Resolve(IAgentVmLookup lookup, string agent = "11111111-1111-1111-1111-111111111111") =>
        new LookupEgressTargetResolver(lookup).ResolveAsync(agent, TestContext.Current.CancellationToken);

    [Fact]
    public async Task It_returns_every_live_vm_name_and_needs_no_channel()
    {
        var target = await Resolve(new Lookup(Vm("bazaar-b", GuestOs.Windows), Vm("bazaar-a", GuestOs.Linux, iface: "tap7")));

        Assert.Equal(["bazaar-a", "bazaar-b"], target.VmNames);
        Assert.Equal("tap7", target.GuestInterface);
    }

    [Fact]
    public async Task Retired_and_uncreated_vms_are_skipped()
    {
        var target = await Resolve(new Lookup(Vm("bazaar-a"), Vm("bazaar-old", retired: true), Vm(null)));

        Assert.Equal(["bazaar-a"], target.VmNames);
    }

    [Fact]
    public async Task Without_interface_info_the_interface_is_null_so_the_nftables_enforcer_fails_closed()
    {
        var target = await Resolve(new Lookup(Vm("bazaar-a")));

        Assert.Null(target.GuestInterface);
        var enforcer = new NftablesEgressEnforcer(new LookupEgressTargetResolver(new Lookup(Vm("bazaar-a"))), new NoRoot());
        await Assert.ThrowsAsync<EgressEnforcementException>(() => enforcer.ApplyAsync("11111111-1111-1111-1111-111111111111", Samples.Rules(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_agent_with_no_live_vm_is_an_error()
    {
        await Assert.ThrowsAsync<EgressEnforcementException>(() => Resolve(new Lookup(Vm("bazaar-old", retired: true))));
        await Assert.ThrowsAsync<EgressEnforcementException>(() => Resolve(new Lookup(), "22222222-2222-2222-2222-222222222222"));
        await Assert.ThrowsAsync<EgressEnforcementException>(() => Resolve(new Lookup(Vm("bazaar-a")), "not-a-guid"));
    }

    [Fact]
    public async Task A_live_vm_with_a_bad_name_is_an_error_not_a_skip()
    {
        await Assert.ThrowsAsync<EgressEnforcementException>(() => Resolve(new Lookup(Vm("bazaar-a"), Vm("bad name"))));
        await Assert.ThrowsAsync<EgressEnforcementException>(() => Resolve(new Lookup(Vm("bazaar-a\n"))));
    }

    [Fact]
    public async Task Two_different_interfaces_are_an_error()
    {
        await Assert.ThrowsAsync<EgressEnforcementException>(() => Resolve(new Lookup(Vm("bazaar-a", iface: "tap1"), Vm("bazaar-b", iface: "tap2"))));
    }

    [Fact]
    public void The_enforcer_uses_the_lookup_when_no_resolver_is_registered_and_stays_unavailable_without_either()
    {
        var withLookup = new ServiceCollection();
        withLookup.AddSingleton<IAgentVmLookup>(new Lookup(Vm("bazaar-a")));
        withLookup.AddBazaarVmEgressEnforcers(EnforcerPlatform.Windows);
        var neither = new ServiceCollection().AddBazaarVmEgressEnforcers(EnforcerPlatform.Windows);

        using var a = withLookup.BuildServiceProvider();
        using var b = neither.BuildServiceProvider();

        Assert.IsType<HyperVEgressEnforcer>(a.GetRequiredService<IEgressEnforcer>());
        Assert.IsType<UnavailableEgressEnforcer>(b.GetRequiredService<IEgressEnforcer>());
    }
}
