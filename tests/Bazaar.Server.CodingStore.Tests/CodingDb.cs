using Bazaar.Server.CodingStore.Data;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Host.Startup;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.CodingStore.Tests;

/// <summary>A cloned test database with the coding namespace applied (0001 tool_calls, 0002 check_runs), read the way the host reads it.</summary>
public sealed class CodingDb : PgFixture, IAsyncLifetime
{
    private NpgsqlDataSource? _pool;

    /// <summary>Pooled data source for this class's database.</summary>
    public new NpgsqlDataSource DataSource => _pool ?? throw new InvalidOperationException("CodingDb is not initialised.");

    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        _pool = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(ConnectionString) { MaxPoolSize = 12 }.ConnectionString);
        var source = new ComponentMigrationSource(typeof(PostgresToolCallStore).Assembly);
        await new MigrationRunner(DataSource, new MigrationOptions(16)).MigrateAsync(source, "coding", CancellationToken.None);
    }

    /// <summary>Runs a statement with positional parameters and returns the rows affected.</summary>
    public async Task<int> ExecAsync(string sql, params object[] args)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        foreach (var a in args)
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        return await cmd.ExecuteNonQueryAsync();
    }
}
