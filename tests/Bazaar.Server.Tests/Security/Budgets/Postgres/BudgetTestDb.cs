using Bazaar.Server.Data.Migrations;
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

    /// <summary>
    /// The connection string this database was opened with, password included. Tests that need their own pools use it:
    /// <see cref="NpgsqlDataSource.ConnectionString"/> omits the password, so a pool built from it cannot log in.
    /// </summary>
    public string ConnectionString { get; }

    private readonly string _name;
    private readonly string _adminConnectionString;

    private BudgetTestDb(string name, string adminConnectionString, string connectionString, NpgsqlDataSource dataSource)
    {
        _name = name;
        _adminConnectionString = adminConnectionString;
        ConnectionString = connectionString;
        DataSource = dataSource;
    }

    public static async Task<BudgetTestDb> CreateAsync(CancellationToken ct)
    {
        var source = await PgSource.GetSharedAsync().ConfigureAwait(false);
        var name = $"budget_{Guid.NewGuid():N}";
        await AdminAsync(source.AdminConnectionString, $"CREATE DATABASE \"{name}\" TEMPLATE template0", ct).ConfigureAwait(false);

        var connectionString = source.ConnectionStringFor(name);
        var dataSource = NpgsqlDataSource.Create(connectionString);
        var db = new BudgetTestDb(name, source.AdminConnectionString, connectionString, dataSource);
        try
        {
            await new MigrationRunner(dataSource).MigrateAsync(
                new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly), MigrateCommand.CurrentProvider(), ct).ConfigureAwait(false);
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
