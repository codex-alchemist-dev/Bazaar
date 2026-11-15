using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Security.Budgets.Postgres;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Security.Budgets.Postgres;

/// <summary>
/// A fresh database per test, created from template0 and migrated with the real runner, so the agent_safety migration runs
/// exactly as in production. Spends from other tests would otherwise leak into a test that counts them.
/// </summary>
internal sealed class BudgetTestDb : IAsyncDisposable
{
    /// <summary>Superuser connection: the test-only owner of the database.</summary>
    public NpgsqlDataSource DataSource { get; }

    private readonly string _name;
    private readonly string _adminConnectionString;

    private BudgetTestDb(string name, string adminConnectionString, NpgsqlDataSource dataSource)
    {
        _name = name;
        _adminConnectionString = adminConnectionString;
        DataSource = dataSource;
    }

    public static async Task<BudgetTestDb> CreateAsync(CancellationToken ct)
    {
        var source = await PgSource.GetSharedAsync().ConfigureAwait(false);
        var name = $"budget_{Guid.NewGuid():N}";
        await AdminAsync(source.AdminConnectionString, $"CREATE DATABASE \"{name}\" TEMPLATE template0", ct).ConfigureAwait(false);

        var dataSource = NpgsqlDataSource.Create(source.ConnectionStringFor(name));
        var db = new BudgetTestDb(name, source.AdminConnectionString, dataSource);
        try
        {
            await new MigrationRunner(dataSource).MigrateAsync(
                new EmbeddedMigrationSource(typeof(PostgresTokenBudgetStore).Assembly), MigrateCommand.CurrentProvider(), ct).ConfigureAwait(false);
        }
        catch
        {
            await db.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return db;
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync().ConfigureAwait(false);
        await AdminAsync(_adminConnectionString, $"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task AdminAsync(string adminConnectionString, string sql, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(adminConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
