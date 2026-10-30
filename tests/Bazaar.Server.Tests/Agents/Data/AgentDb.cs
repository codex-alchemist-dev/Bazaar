using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Agents.Data;

/// <summary>
/// A per-class database with only the agent runtime migration (0010) applied. 0010 has no foreign keys outside its own
/// tables, so it runs without the cluster-wide roles of 0001.
/// </summary>
public sealed class AgentDb : PgFixture, IAsyncLifetime
{
    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        var files = new EmbeddedMigrationSource(typeof(Bazaar.Server.Agents.AgentState).Assembly).Load(MigrationRunner.Common);
        var source = new InMemoryMigrationSource(files.Where(f => f.Version == 10));
        await new MigrationRunner(DataSource).MigrateAsync(source, MigrationRunner.Common);
    }

    /// <summary>Runs one statement against this class's database. Used by tests that need to age a lease or a claim.</summary>
    public async Task ExecAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}
