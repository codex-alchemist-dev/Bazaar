using Bazaar.Server.Host.Startup;

namespace Bazaar.Server.Host.Tests;

public sealed class AgentRoleMigrationTests
{
    [Fact]
    public void Host_migration_source_adds_a_nullable_role_column_to_the_agent_table()
    {
        var source = HostMigrations.Source();

        Assert.Contains("agents", source.Namespaces());
        var file = Assert.Single(source.Load("agents"), f => f.Version == 2);
        Assert.Equal("agent_role", file.Name);
        Assert.Contains("ALTER TABLE agent ADD COLUMN IF NOT EXISTS role text NULL", file.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Host_migration_source_adds_a_nullable_model_column_to_the_agent_table()
    {
        var file = Assert.Single(HostMigrations.Source().Load("agents"), f => f.Version == 3);
        Assert.Equal("agent_model", file.Name);
        Assert.Contains("ALTER TABLE agent ADD COLUMN IF NOT EXISTS model text NULL", file.Sql, StringComparison.Ordinal);
    }
}
