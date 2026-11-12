using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.Components;
using Bazaar.Server.Kanban;
using Bazaar.Server.Kanban.Data;
using Bazaar.Server.Security.Policy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Tests.Kanban;

public class KanbanComponentTests
{
    [Fact]
    public void Kanban_manifest_is_a_feature_with_no_dependencies()
    {
        var manifest = new KanbanComponent().Manifest;

        Assert.Equal("kanban", manifest.Id);
        Assert.Equal(ComponentKind.Feature, manifest.Kind);
        Assert.Empty(manifest.DependsOn);
    }

    [Fact]
    public void Kanban_services_resolve_once_the_kernel_provides_a_data_source()
    {
        // NpgsqlDataSource.Create does not connect, so no database is needed to resolve the service graph.
        using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=bazaar");
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);

        services.AddBazaarComponents(new ConfigurationBuilder().Build(), [new KanbanComponent()]);

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<TaskService>());
        Assert.Same(provider.GetRequiredService<TaskRepository>(), provider.GetRequiredService<TaskRepository>());
    }

    [Fact]
    public void Kanban_transition_is_registered_as_a_runnable_tool_under_its_model_name()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=bazaar");
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddBazaarComponents(new ConfigurationBuilder().Build(), [new KanbanComponent()]);
        using var provider = services.BuildServiceProvider();

        // The turn path builds its registry exactly this way (AgentTurnsComponent).
        var registration = Assert.Single(provider.GetServices<ToolRegistration>());
        var registry = new ToolRegistry([(registration.Name, registration.Tool)]);

        Assert.True(registry.TryGet("kanban.transition", out var tool));
        Assert.Same(provider.GetRequiredService<KanbanTransitionTool>(), tool);
    }

    [Fact]
    public void Kanban_transition_has_a_policy_entry_so_the_fail_closed_policy_can_allow_it()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=bazaar");
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddBazaarComponents(new ConfigurationBuilder().Build(), [new KanbanComponent()]);
        using var provider = services.BuildServiceProvider();

        var capabilities = CapabilityRegistry.Create(provider.GetServices<ToolCapability>());

        Assert.True(capabilities.TryGet(KanbanTransitionTool.ToolName, out var capability));
        Assert.Equal(ToolTier.AutonomousWithNotice, capability.Tier);
        Assert.Equal(SessionRoles.Actor, capability.Roles);
        Assert.NotNull(capability.Rate);
    }
}
