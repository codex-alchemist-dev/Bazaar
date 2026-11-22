using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Host.Startup;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Setup.FirstRun.Postgres.Tests;

/// <summary>A cloned database migrated with the core schema (which includes remote), the agents and the first-run migrations.</summary>
public sealed class FirstRunDb : PgFixture, IAsyncLifetime
{
    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost) return;
        await base.InitializeAsync();
        await new MigrationRunner(DataSource, new MigrationOptions(16))
            .MigrateAsync(
                new CompositeMigrationSource(
                    new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly),
                    new ComponentMigrationSource(typeof(PgAgentFirstRunSeeds).Assembly),
                    new ComponentMigrationSource(typeof(PostgresFirstRunStore).Assembly)),
                "linux",
                CancellationToken.None);
    }

    public async Task<Guid> InsertOwnerAsync()
    {
        var id = Guid.CreateVersion7();
        await using var cmd = DataSource.CreateCommand("INSERT INTO human (id, display_name, role, status) VALUES ($1, 'Owner', 'owner', 'active')");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    public async Task<T> ScalarAsync<T>(string sql, params object[] args)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        foreach (var a in args) cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}
