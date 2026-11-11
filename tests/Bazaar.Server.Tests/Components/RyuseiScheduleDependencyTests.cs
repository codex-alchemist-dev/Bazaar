using Bazaar.Server.Components;
using Bazaar.Server.Kanban.Ryusei;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Tests.Components;

/// <summary>
/// RyuseiFuda's schedule component (doc 38, layer 2) resolves services from the GC log and the audit log at registration
/// and at run time. Those reads must be declared in its manifest, or the kernel starts it without the components it needs.
/// Audit doc 40, section 3.
/// </summary>
public sealed class RyuseiScheduleDependencyTests
{
    private sealed class StubComponent(ComponentManifest manifest) : IBazaarComponent
    {
        public ComponentManifest Manifest { get; } = manifest;

        public void AddServices(IServiceCollection services, IConfiguration configuration)
        {
        }
    }

    [Fact]
    public void Ryusei_declares_the_components_it_resolves_from()
    {
        var depends = RyuseiScheduleComponent.ManifestDefinition.DependsOn;

        Assert.Contains("kanban", depends);
        Assert.Contains("gc", depends);
        Assert.Contains("audit", depends);
    }

    [Fact]
    public void Ryusei_keeps_the_old_enablement_key_working()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Bazaar:Components:ryusei-schedule:Enabled"] = "false" })
            .Build();

        Assert.False(BazaarComponentRegistration.IsEnabled(configuration, "ryusei"));
    }

    [Fact]
    public void Ryusei_will_not_start_when_its_gc_component_is_disabled()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Bazaar:Components:gc:Enabled"] = "false" })
            .Build();
        var components = new IBazaarComponent[]
        {
            new StubComponent(new ComponentManifest("gc", "0.1.0", ComponentKind.Feature, [], [])),
            new StubComponent(new ComponentManifest("audit", "0.1.0", ComponentKind.Feature, [], [])),
            new StubComponent(new ComponentManifest("kanban", "0.1.0", ComponentKind.Feature, [], [])),
            new RyuseiScheduleComponent(),
        };

        var ex = Assert.Throws<ComponentGraphException>(() =>
            new ServiceCollection().AddBazaarComponents(configuration, components));

        Assert.Contains("ryusei", ex.Message, StringComparison.Ordinal);
        Assert.Contains("gc", ex.Message, StringComparison.Ordinal);
    }
}
