using Bazaar.Core;
using Bazaar.Server.VmDisplay.Abstractions;
using Bazaar.Server.Vms.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.VmDisplay.AgentTools.Vms.Tests;

public class ComponentTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private sealed class EmptyDirectory : IAgentVmDirectory
    {
        public Task<IReadOnlyList<AgentVmInfo>> ListForAgentAsync(Guid agentId, CancellationToken ct) => Task.FromResult<IReadOnlyList<AgentVmInfo>>([]);

        public Task<IReadOnlyList<AgentVmInfo>> ListRunningAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AgentVmInfo>>([]);
    }

    [Fact]
    public void The_manifest_is_an_adapter_with_no_hard_dependency_so_it_registers_before_the_tools()
    {
        ComponentManifest m = new VmAgentDisplayTargetsComponent().Manifest;

        Assert.Equal("vm-display-agent-targets", m.Id);
        Assert.Equal(ComponentKind.Adapter, m.Kind);
        Assert.Empty(m.DependsOn);
        Assert.Equal(["vms"], m.Soft);
        // Components with no hard edge between them start in id order: this id must sort before the tools component's id.
        Assert.True(string.CompareOrdinal(m.Id, "vm-display-agent-tools") < 0);
    }

    [Fact]
    public void By_default_nothing_is_registered_so_no_screen_tool_can_be_offered()
    {
        var services = new ServiceCollection();

        new VmAgentDisplayTargetsComponent().AddServices(services, Config());

        Assert.Empty(services);
    }

    [Fact]
    public void An_explicit_false_registers_nothing()
    {
        var services = new ServiceCollection();

        new VmAgentDisplayTargetsComponent().AddServices(services, Config(("Bazaar:VmDisplayAgentTargets:Enabled", "false")));

        Assert.Empty(services);
    }

    [Fact]
    public void When_enabled_the_vm_adapter_is_registered_over_the_directory()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentVmDirectory>(new EmptyDirectory());

        new VmAgentDisplayTargetsComponent().AddServices(services, Config(("Bazaar:VmDisplayAgentTargets:Enabled", "true")));
        using ServiceProvider sp = services.BuildServiceProvider();

        Assert.IsType<VmAgentDisplayTargets>(sp.GetRequiredService<IAgentDisplayTargets>());
    }

    [Fact]
    public async Task When_enabled_without_a_directory_every_agent_gets_no_target()
    {
        var services = new ServiceCollection();
        new VmAgentDisplayTargetsComponent().AddServices(services, Config(("Bazaar:VmDisplayAgentTargets:Enabled", "true")));
        using ServiceProvider sp = services.BuildServiceProvider();

        Assert.Null(await sp.GetRequiredService<IAgentDisplayTargets>().ResolveAsync(Guid.NewGuid(), AgentDisplayKind.Linux, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_registration_the_host_made_itself_wins()
    {
        var services = new ServiceCollection();
        var own = new OwnTargets();
        services.AddSingleton<IAgentDisplayTargets>(own);

        new VmAgentDisplayTargetsComponent().AddServices(services, Config(("Bazaar:VmDisplayAgentTargets:Enabled", "true")));
        using ServiceProvider sp = services.BuildServiceProvider();

        Assert.Same(own, sp.GetRequiredService<IAgentDisplayTargets>());
    }

    [Theory]
    [InlineData("Enabled", "maybe")]
    [InlineData("ReadTimeoutSeconds", "x")]
    [InlineData("ReadTimeoutSeconds", "0")]
    [InlineData("ReadTimeoutSeconds", "61")]
    public void A_bad_setting_fails_at_registration_even_while_off(string key, string value)
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() =>
            new VmAgentDisplayTargetsComponent().AddServices(services, Config(("Bazaar:VmDisplayAgentTargets:" + key, value))));
    }

    private sealed class OwnTargets : IAgentDisplayTargets
    {
        public Task<DisplayTarget?> ResolveAsync(Guid agentId, AgentDisplayKind kind, CancellationToken cancellationToken) => Task.FromResult<DisplayTarget?>(null);
    }
}
