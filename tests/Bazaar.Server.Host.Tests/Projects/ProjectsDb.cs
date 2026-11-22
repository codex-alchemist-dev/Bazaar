using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Projects;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Host.Tests.Projects;

/// <summary>
/// A cloned test database migrated with the server's common schema (org_relation, audit_log) and the <c>projects</c> component
/// namespace, the same composition the host runs for those two.
/// </summary>
public sealed class ProjectsDb : PgFixture, IAsyncLifetime
{
    private NpgsqlDataSource? _pool;

    /// <summary>Pooled data source for this class's database, capped at 8 connections.</summary>
    public new NpgsqlDataSource DataSource => _pool ?? throw new InvalidOperationException("ProjectsDb is not initialised.");

    public override async ValueTask InitializeAsync()
    {
        // Windows without BAZAAR_TEST_PG has no database; each test skips itself.
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        _pool = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(ConnectionString) { MaxPoolSize = 8 }.ConnectionString);
        var source = new CompositeMigrationSource(
            new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly),
            new ComponentMigrationSource(typeof(ProjectsComponent).Assembly));
        await new MigrationRunner(DataSource, new MigrationOptions(16)).MigrateAsync(source, "linux", CancellationToken.None);
    }

    /// <summary>Removes every project, member, loan and audit row so each test starts empty.</summary>
    public async Task ClearAsync()
    {
        await using var cmd = DataSource.CreateCommand("TRUNCATE project_member, project, org_relation");
        await cmd.ExecuteNonQueryAsync();
    }
}
