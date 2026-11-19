using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Host.Startup;

namespace Bazaar.Server.Host.Tests;

public sealed class HostMigrationsTests
{
    [Fact]
    public void Composed_plan_includes_the_gateway_grants_migration()
    {
        // Plan is pure (no database), so this checks that the host's composed source discovers the gateway namespace.
        var plan = MigrationRunner.Plan(HostMigrations.Source(), MigrationNamespaces.Common, HostComponents.All.Select(c => c.Manifest));

        Assert.Contains(plan, f => f.Namespace == "gateway" && f.Version == 1 && f.Name == "gateway_grants");
    }
}
