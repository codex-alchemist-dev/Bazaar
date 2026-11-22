using Bazaar.Core;
using Bazaar.Server.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Conduit.Tests;

public sealed class ConduitComponentTests
{
    [Fact]
    public void Manifest_is_a_feature_with_no_hard_dependencies_and_orders_on_its_own()
    {
        var manifest = new ConduitComponent().Manifest;

        Assert.Equal("conduit", manifest.Id);
        Assert.Equal(ComponentKind.Feature, manifest.Kind);
        Assert.Empty(manifest.DependsOn);
        Assert.Single(ComponentGraph.Order([manifest]));
    }

    [Fact]
    public void Component_contributes_every_conduit_key_to_the_settings_registry()
    {
        var services = new ServiceCollection();
        new ConduitComponent().AddServices(services, new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var registry = SettingRegistry.Combine([], provider.GetServices<ISettingContribution>());

        Assert.True(registry.TryGet("conduit.cloudflared.enabled", out var key));
        Assert.NotNull(key);
        Assert.Equal(Conduit.Settings.ConduitSettings.All.Count, registry.Definitions.Count);
    }
}
