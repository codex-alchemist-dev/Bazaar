using Bazaar.Server.Components;
using Bazaar.Server.Kanban;
using Bazaar.Server.Kanban.Data;
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
}
