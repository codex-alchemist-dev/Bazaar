using Bazaar.Contracts.Gateway;
using Bazaar.Core;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.VmDisplay.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.VmDisplay.AgentTools.Tests;

public class ComponentTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static ServiceCollection Build(bool display, bool targets, bool native = false, IConfiguration? config = null)
    {
        var services = new ServiceCollection();
        if (display)
            services.AddSingleton<IGuestDisplay>(new FakeDisplay());
        if (targets)
            services.AddSingleton<IAgentDisplayTargets>(new FakeTargets());
        if (native)
            services.AddSingleton<IDisplayNativeSize>(new FakeNative((100, 100)));
        new VmDisplayAgentToolsComponent().AddServices(services, config ?? Config());
        return services;
    }

    [Fact]
    public void Manifest_is_a_feature_with_only_a_soft_dependency()
    {
        ComponentManifest m = new VmDisplayAgentToolsComponent().Manifest;
        Assert.Equal("vm-display-agent-tools", m.Id);
        Assert.Equal(ComponentKind.Feature, m.Kind);
        Assert.Empty(m.DependsOn);
        Assert.Equal(["vm-display"], m.Soft);
    }

    [Fact]
    public void Both_tools_and_their_definitions_are_registered_when_the_dependencies_exist()
    {
        using ServiceProvider sp = Build(display: true, targets: true).BuildServiceProvider();

        string[] names = sp.GetServices<ToolRegistration>().Select(r => r.Name).Order().ToArray();
        Assert.Equal(["screen.capture", "screen.input"], names);
        Assert.Equal(["screen.capture", "screen.input"], sp.GetServices<ModelToolDefinition>().Select(d => d.Name).Order().ToArray());
        Assert.All(sp.GetServices<ToolRegistration>(), r => Assert.IsAssignableFrom<IContextualTool>(r.Tool));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Nothing_is_registered_when_a_dependency_is_missing(bool display, bool targets)
    {
        using ServiceProvider sp = Build(display, targets).BuildServiceProvider();

        Assert.Empty(sp.GetServices<ToolRegistration>());
        Assert.Empty(sp.GetServices<ModelToolDefinition>());
    }

    [Fact]
    public void The_optional_native_size_port_is_picked_up_when_present()
    {
        using ServiceProvider sp = Build(display: true, targets: true, native: true).BuildServiceProvider();
        Assert.Equal(2, sp.GetServices<ToolRegistration>().Count());
    }

    [Fact]
    public void Options_bind_from_the_section_and_a_bad_value_fails_at_start()
    {
        using ServiceProvider sp = Build(true, true, config: Config(
            ("Bazaar:VmDisplay:AgentTools:MaxImageBytes", "2048"),
            ("Bazaar:VmDisplay:AgentTools:MaxActionsPerCall", "7"))).BuildServiceProvider();
        var o = sp.GetRequiredService<ScreenToolsOptions>();
        Assert.Equal(2048, o.MaxImageBytes);
        Assert.Equal(7, o.MaxActionsPerCall);
        Assert.Equal(500, o.MaxTextLength);

        Assert.Throws<ArgumentOutOfRangeException>(() => Build(false, false, config: Config(("Bazaar:VmDisplay:AgentTools:MaxActionsPerCall", "51"))));
    }

    [Fact]
    public void Defaults_match_the_brief()
    {
        var o = new ScreenToolsOptions();
        Assert.Equal(1024 * 1024, o.MaxImageBytes);
        Assert.Equal(20, o.MaxActionsPerCall);
    }
}
