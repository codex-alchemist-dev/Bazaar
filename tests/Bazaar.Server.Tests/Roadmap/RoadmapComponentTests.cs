using Bazaar.Server.Components;
using Bazaar.Server.Roadmap;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Tests.Roadmap;

/// <summary>The roadmap component declares its dependencies and registers the snapshot service. No database is opened.</summary>
public sealed class RoadmapComponentTests
{
    [Fact]
    public void Manifest_depends_on_the_vault_and_kanban_and_provides_the_roadmap()
    {
        var manifest = RoadmapComponent.ManifestDefinition;

        Assert.Equal("ryusei-roadmap", manifest.Id);
        Assert.Equal(["vault", "kanban"], manifest.DependsOn);
        Assert.Contains("feature:ryusei-roadmap", manifest.Provides);
    }

    [Fact]
    public void AddServices_registers_the_snapshot_service_over_the_shared_data_source()
    {
        var services = new ServiceCollection();
        services.AddSingleton(NpgsqlDataSource.Create("Host=localhost;Database=unused"));

        new RoadmapComponent().AddServices(services, new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<RoadmapSnapshotService>());
    }
}
