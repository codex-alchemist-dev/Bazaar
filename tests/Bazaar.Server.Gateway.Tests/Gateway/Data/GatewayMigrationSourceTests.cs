using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Gateway.Data;

namespace Bazaar.Server.Tests.Gateway.Data;

public class GatewayMigrationSourceTests
{
    // A file outside the namespace folder (or a missing EmbeddedResource item) is not an error for the runner: it is silently not
    // embedded. These tests fail when the gateway grants migration stops being embedded or discovered.
    [Fact]
    public void Gateway_namespace_with_grants_migration_0001_is_embedded()
    {
        var source = new EmbeddedMigrationSource(typeof(UsageRepository).Assembly);

        Assert.Contains("gateway", source.Namespaces());
        Assert.Contains(source.Load("gateway"), f => f.Version == 1 && f.Name == "gateway_grants");
    }

    [Fact]
    public void Runner_plan_includes_the_gateway_grants_migration()
    {
        var source = new EmbeddedMigrationSource(typeof(UsageRepository).Assembly);

        var plan = MigrationRunner.Plan(source, MigrationNamespaces.Common, []);

        // The gateway namespace now has two files (0001 grants, 0002 model kind); both must be planned, grants first.
        var gateway = plan.Where(f => f.Namespace == "gateway").ToList();
        Assert.Equal(2, gateway.Count);
        var file = Assert.Single(gateway, f => f.Version == 1 && f.Name == "gateway_grants");
        Assert.Equal("gateway", file.Namespace);
        Assert.Equal([1, 2], gateway.Select(f => f.Version));
        Assert.Equal(["gateway_grants", "gateway_model_kind"], gateway.Select(f => f.Name));
    }

    [Fact]
    public void Grants_migration_grants_only_insert_select_and_update_to_the_app_role()
    {
        var file = Assert.Single(
            new EmbeddedMigrationSource(typeof(UsageRepository).Assembly).Load("gateway"),
            f => f.Version == 1 && f.Name == "gateway_grants");

        Assert.Contains("GRANT SELECT, INSERT ON gw_usage TO bazaar_app;", file.Sql);
        Assert.Contains("GRANT SELECT, INSERT, UPDATE ON gw_provider, gw_model, gw_route, gw_quota_state, agent_model_policy TO bazaar_app;", file.Sql);
        // Checks the GRANT statements only; the comments in the file may name DELETE and TRUNCATE.
        var grants = file.Sql.Split('\n').Where(line => line.TrimStart().StartsWith("GRANT", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(2, grants.Count);
        Assert.All(grants, grant =>
        {
            Assert.DoesNotContain("DELETE", grant, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TRUNCATE", grant, StringComparison.OrdinalIgnoreCase);
        });
    }
}
