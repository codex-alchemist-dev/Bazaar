using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Council.Data;

/// <summary>
/// A cloned test database with the core GC schema (0001, for <c>gc</c> and the append-only trigger function) and the
/// Council tables (council/0001). Council needs nothing else, so the vault migration (0009, which needs pgvector halfvec) is not applied.
/// </summary>
public sealed class CouncilDb : PgFixture, IAsyncLifetime
{
    private NpgsqlDataSource? _pool;

    /// <summary>Pooled data source for this class's database, capped at 12 connections.</summary>
    public new NpgsqlDataSource DataSource => _pool ?? throw new InvalidOperationException("CouncilDb is not initialised.");

    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        _pool = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(ConnectionString) { MaxPoolSize = 12 }.ConnectionString);
        var files = new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly).Load(MigrationRunner.Common);
        var council = new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly).Load("council");
        var source = new InMemoryMigrationSource(files.Where(f => f.Version == 1).Concat(council));
        await new MigrationRunner(DataSource, new MigrationOptions(16)).MigrateAsync(source, "council", CancellationToken.None);
    }

    /// <summary>Creates a GC for vote origins and returns its id.</summary>
    public async Task<Guid> CreateGcAsync()
    {
        var gcId = Guid.CreateVersion7();
        await using var cmd = DataSource.CreateCommand("INSERT INTO gc (gc_id, type_key, title, created_by) VALUES ($1, 'general', 'council', $2)");
        cmd.Parameters.AddWithValue(gcId);
        cmd.Parameters.AddWithValue(Guid.CreateVersion7());
        await cmd.ExecuteNonQueryAsync();
        return gcId;
    }

    /// <summary>Runs a scalar query with positional parameters ($1, $2, ...).</summary>
    public async Task<T> ScalarAsync<T>(string sql, params object[] args)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        foreach (var a in args)
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}
