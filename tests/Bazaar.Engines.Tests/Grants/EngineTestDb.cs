using Bazaar.Engines.Grants;
using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Engines.Tests.Grants;

/// <summary>
/// A fresh database per test, created from template0 and migrated with the real runner (only the engines namespace).
/// Runs only when <c>BAZAAR_TEST_PG</c> is set; callers skip first with <see cref="Enabled"/>.
/// </summary>
internal sealed class EngineTestDb : IAsyncDisposable
{
    private readonly NpgsqlDataSource _admin;
    private readonly string _name;

    private EngineTestDb(string name, NpgsqlDataSource admin, NpgsqlDataSource dataSource)
    {
        _name = name;
        _admin = admin;
        DataSource = dataSource;
    }

    /// <summary>True when the test server is configured through <c>BAZAAR_TEST_PG</c>. The tests never start a container.</summary>
    public static bool Enabled => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PgSource.EnvConnectionString));

    /// <summary>The migrated database for this test.</summary>
    public NpgsqlDataSource DataSource { get; }

    public static async Task<EngineTestDb> CreateAsync(CancellationToken ct)
    {
        var source = await PgSource.GetSharedAsync().ConfigureAwait(false);
        var name = $"engines_{Guid.NewGuid():N}";
        var admin = NpgsqlDataSource.Create(source.AdminConnectionString);
        await using (var create = admin.CreateCommand($"CREATE DATABASE \"{name}\" TEMPLATE template0"))
        {
            await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var dataSource = NpgsqlDataSource.Create(source.ConnectionStringFor(name));
        var db = new EngineTestDb(name, admin, dataSource);
        try
        {
            await new MigrationRunner(dataSource).MigrateAsync(
                new EmbeddedMigrationSource(typeof(EngineGrantStore).Assembly), MigrateCommand.CurrentProvider(), ct).ConfigureAwait(false);
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
        GC.SuppressFinalize(this);
        await DataSource.DisposeAsync().ConfigureAwait(false);
        await using (var drop = _admin.CreateCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)"))
        {
            await drop.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await _admin.DisposeAsync().ConfigureAwait(false);
    }
}
