using Bazaar.Engines.Grants;
using Bazaar.Server.Data.Migrations;

namespace Bazaar.Engines.Tests.Data;

public class EngineMigrationSourceTests
{
    // A file outside the namespace folder (for example Data/Sql/components/engines/) is not an error for the runner: it is
    // silently not embedded. This test fails if the engines migration stops being discovered.
    [Fact]
    public void Engines_namespace_with_migration_0001_is_embedded()
    {
        var source = new EmbeddedMigrationSource(typeof(EngineGrantStore).Assembly);

        Assert.Contains("engines", source.Namespaces());
        var files = source.Load("engines");
        Assert.Contains(files, f => f.Version == 1 && f.Name == "engine_registry");
    }
}
