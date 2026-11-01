using Bazaar.Server.Components;
using Bazaar.Server.Host.Components.Audit;
using Bazaar.Server.Host.Components.Settings;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests;

public sealed class SettingsAuditComponentTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void Settings_and_audit_manifests_are_feature_components_with_the_expected_ids()
    {
        Assert.Equal("settings", SettingsComponent.ManifestDefinition.Id);
        Assert.Equal("audit", AuditComponent.ManifestDefinition.Id);
        Assert.Equal(ComponentKind.Feature, SettingsComponent.ManifestDefinition.Kind);
        Assert.Equal(ComponentKind.Feature, AuditComponent.ManifestDefinition.Kind);
        Assert.Equal(["feature:settings"], SettingsComponent.ManifestDefinition.Provides);
        Assert.Equal(["feature:audit"], AuditComponent.ManifestDefinition.Provides);
    }

    [Fact]
    public void Host_component_ids_are_unique()
    {
        var ids = HostApplicationExtensions.HostComponents().Select(c => c.Manifest.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Settings_depends_on_audit_so_the_graph_orders_audit_first()
    {
        Assert.Equal(["audit"], SettingsComponent.ManifestDefinition.DependsOn);
        Assert.Empty(AuditComponent.ManifestDefinition.DependsOn);

        var order = ComponentGraph.Order(HostApplicationExtensions.HostComponents().Select(c => c.Manifest)).Select(m => m.Id).ToList();

        Assert.True(order.IndexOf("audit") < order.IndexOf("settings"));
    }

    [Fact]
    public void Disabled_audit_is_not_registered()
    {
        var services = new ServiceCollection();

        services.AddBazaarComponents(
            Config(("Bazaar:Components:audit:Enabled", "false"), ("Bazaar:Components:settings:Enabled", "false")),
            [new AuditComponent(), new SettingsComponent()]);

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IAuditLog));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ISettingStore));
    }

    [Fact]
    public void Enabled_settings_with_disabled_audit_fails_the_start_and_names_audit()
    {
        // Settings' audit sink appends to IAuditLog, so settings cannot start without audit. The kernel reports it at start.
        var services = new ServiceCollection();

        var ex = Assert.Throws<ComponentGraphException>(() => services.AddBazaarComponents(
            Config(("Bazaar:Components:audit:Enabled", "false")),
            [new AuditComponent(), new SettingsComponent()]));

        Assert.Contains("'settings' needs disabled component(s): audit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Enabled_settings_and_audit_register_the_store_and_the_audit_sink()
    {
        // Registration only describes services; the shared data source is a factory that runs at first resolve, so no database is needed.
        var services = new ServiceCollection();

        services.AddBazaarComponents(Config(), [new AuditComponent(), new SettingsComponent()]);

        Assert.Contains(services, d => d.ServiceType == typeof(ISettingStore));
        Assert.Contains(services, d => d.ServiceType == typeof(ISettingAuditSink));
        Assert.Contains(services, d => d.ServiceType == typeof(IAuditLog));
    }
}
