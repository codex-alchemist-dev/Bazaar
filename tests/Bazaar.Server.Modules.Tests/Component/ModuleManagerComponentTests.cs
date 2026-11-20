using Bazaar.Server.Modules;
using Bazaar.Server.Modules.Releases;
using Bazaar.Server.Modules.Settings;
using Bazaar.Server.Modules.Updates;
using Bazaar.Server.Settings;
using Cdax.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Modules.Tests.Component;

public class ModuleManagerComponentTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void Manifest_is_a_feature_with_the_module_manager_id_and_no_hard_dependencies()
    {
        var manifest = new ModuleManagerComponent().Manifest;

        Assert.Equal("module-manager", manifest.Id);
        Assert.Equal(ComponentKind.Feature, manifest.Kind);
        Assert.Empty(manifest.DependsOn);
        Assert.Equal(["feature:module-manager"], manifest.Provides);
    }

    [Fact]
    public void Manifest_is_valid_in_the_component_graph_on_its_own()
    {
        var ordered = ComponentGraph.Order([new ModuleManagerComponent().Manifest]);

        Assert.Single(ordered);
        Assert.Equal("module-manager", ordered[0].Id);
    }

    [Fact]
    public void Manifest_orders_after_its_dependencies_when_a_component_declares_it()
    {
        // A component that depends on the module manager must come after it, so the manager's services exist first.
        var dependent = new ComponentManifest("module-dependent", "0.1.0", ComponentKind.Feature, ["module-manager"], []);

        var ordered = ComponentGraph.Order([dependent, new ModuleManagerComponent().Manifest]);

        Assert.Equal(["module-manager", "module-dependent"], ordered.Select(m => m.Id));
    }

    [Fact]
    public void Component_resolves_the_update_policy_evaluator_and_defer_aging_without_a_host()
    {
        var services = new ServiceCollection();
        services.AddCdaxModules(Config(), [new ModuleManagerComponent()]);

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<UpdatePolicyEvaluator>());
        Assert.NotNull(provider.GetRequiredService<DeferAging>());
    }

    [Fact]
    public void Component_contributes_every_module_manager_setting_key()
    {
        var services = new ServiceCollection();
        services.AddCdaxModules(Config(), [new ModuleManagerComponent()]);

        using var provider = services.BuildServiceProvider();
        var contributions = provider.GetServices<ISettingContribution>().ToList();

        var contribution = Assert.Single(contributions, c => c is ModuleManagerSettingContribution);
        Assert.Same(ModuleManagerSettings.All, contribution.Settings);
    }

    [Fact]
    public void Component_resolves_the_release_candidate_lifecycle()
    {
        var services = new ServiceCollection();
        services.AddCdaxModules(Config(), [new ModuleManagerComponent()]);

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<ReleaseCandidateLifecycle>());
    }

    [Fact]
    public void Component_uses_the_system_clock_when_no_time_provider_is_registered()
    {
        var services = new ServiceCollection();
        services.AddCdaxModules(Config(), [new ModuleManagerComponent()]);

        using var provider = services.BuildServiceProvider();
        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void Component_keeps_a_time_provider_the_host_already_registered()
    {
        var host = new FixedTimeProvider(new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(host);

        services.AddCdaxModules(Config(), [new ModuleManagerComponent()]);

        using var provider = services.BuildServiceProvider();
        Assert.Same(host, provider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void Component_reads_the_clock_it_was_given_for_defer_aging()
    {
        var now = new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(now));
        services.AddCdaxModules(Config(), [new ModuleManagerComponent()]);

        using var provider = services.BuildServiceProvider();
        var report = provider.GetRequiredService<DeferAging>().Measure(now.AddDays(31), deferWarnDays: 30);

        Assert.Equal(0, report.AgeDays);
        Assert.False(report.IsAging);
    }

    [Fact]
    public void Disabled_component_registers_nothing()
    {
        var services = new ServiceCollection();
        var before = services.Count;

        services.AddCdaxModules(
            Config(("Cdax:Modules:module-manager:Enabled", "false")),
            [new ModuleManagerComponent()]);

        Assert.Equal(before, services.Count);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(UpdatePolicyEvaluator));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(DeferAging));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ReleaseCandidateLifecycle));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(TimeProvider));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
