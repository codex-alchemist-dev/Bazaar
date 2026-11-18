using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Security.Budgets.Postgres;
using Npgsql;

namespace Bazaar.Server.AgentSafety.Tests;

/// <summary>Migrates a test database with the server's own embedded migrations, so the same SQL runs as in production.</summary>
internal static class ServerMigrations
{
    public static Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct) =>
        new MigrationRunner(dataSource).MigrateAsync(
            new EmbeddedMigrationSource(typeof(PostgresTokenBudgetStore).Assembly), MigrateCommand.CurrentProvider(), ct);
}
