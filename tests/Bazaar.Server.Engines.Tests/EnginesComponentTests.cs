using Bazaar.Server.EngineDispatch;
using Bazaar.Server.Engines;
using Bazaar.Server.Engines.Grants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Engines.Tests;

public sealed class EnginesComponentTests
{
    [Fact]
    public void Registers_the_postgres_grant_reader_over_the_host_data_source()
    {
        var services = new ServiceCollection();
        using var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=none");
        services.AddSingleton(dataSource);
        new EnginesComponent().AddServices(services, new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();

        Assert.IsType<PostgresEngineGrantReader>(provider.GetRequiredService<IEngineGrantReader>());
        Assert.Equal("engines", EnginesComponent.ManifestDefinition.MigrationNamespace);
    }
}
