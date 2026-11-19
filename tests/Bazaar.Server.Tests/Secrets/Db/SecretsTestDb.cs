using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Secrets.Postgres;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Secrets.Db;

/// <summary>A fresh database per test, migrated with the real runner, so the secrets/0001 migration runs as in production.</summary>
internal sealed class SecretsTestDb : IAsyncDisposable
{
    private readonly string _name;
    private readonly string _adminConnectionString;

    private SecretsTestDb(string name, string adminConnectionString, NpgsqlDataSource dataSource)
    {
        _name = name;
        _adminConnectionString = adminConnectionString;
        DataSource = dataSource;
    }

    public NpgsqlDataSource DataSource { get; }

    public static async Task<SecretsTestDb> CreateAsync(CancellationToken ct)
    {
        var source = await PgSource.GetSharedAsync().ConfigureAwait(false);
        var name = $"secrets_{Guid.NewGuid():N}";
        await AdminAsync(source.AdminConnectionString, $"CREATE DATABASE \"{name}\" TEMPLATE template0", ct).ConfigureAwait(false);

        var dataSource = NpgsqlDataSource.Create(source.ConnectionStringFor(name));
        var db = new SecretsTestDb(name, source.AdminConnectionString, dataSource);
        try
        {
            await new MigrationRunner(dataSource).MigrateAsync(
                new EmbeddedMigrationSource(typeof(PostgresSecretRecordRepository).Assembly), MigrateCommand.CurrentProvider(), ct).ConfigureAwait(false);
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
