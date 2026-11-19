using Bazaar.GitHub;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Vms.Environments;

namespace Bazaar.Server.Host.Startup;

/// <summary>The migration files the host runs: the server's own, and the Vms and GitHub modules' component files under Sql/components/vms and Sql/components/github.</summary>
public static class HostMigrations
{
    /// <summary>Server, Vms and GitHub migrations, read from their assemblies.</summary>
    public static IMigrationSource Source() => new CompositeMigrationSource(
        new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly),
        new ComponentMigrationSource(typeof(EnvironmentRepository).Assembly),
        new ComponentMigrationSource(typeof(GitHubComponent).Assembly));
}
