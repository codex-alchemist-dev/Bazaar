using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Skimmer;

/// <summary>A cloned test database migrated with every common migration (0001 to 0016), for the Skimmer gate tables.</summary>
public sealed class SkimmerDb : PgFixture, IAsyncLifetime
{
    private NpgsqlDataSource? _pool;

    /// <summary>Pooled data source for this class's database.</summary>
    public new NpgsqlDataSource DataSource => _pool ?? throw new InvalidOperationException("SkimmerDb is not initialised.");

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _pool = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(ConnectionString) { MaxPoolSize = 8 }.ConnectionString);
        await new MigrationRunner(DataSource, new MigrationOptions(16))
            .MigrateAsync(new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly), "linux", CancellationToken.None);
    }

    /// <summary>Inserts an active agent and returns its id. The agent table is migration 0010.</summary>
    public async Task<Guid> CreateAgentAsync()
    {
        var agentId = Guid.CreateVersion7();
        await using var cmd = DataSource.CreateCommand("INSERT INTO agent (agent_id, name, state) VALUES ($1, 'Charles', 'active')");
        cmd.Parameters.AddWithValue(agentId);
        await cmd.ExecuteNonQueryAsync();
        return agentId;
    }

    /// <summary>Creates a GC and one message event (seq 1) in it, and returns the GC id. Wake decisions point at these events.</summary>
    public async Task<(Guid GcId, long Seq)> CreateGcEventAsync()
    {
        var gcId = Guid.CreateVersion7();
        await using (var gc = DataSource.CreateCommand("INSERT INTO gc (gc_id, type_key, title, created_by) VALUES ($1, 'general', 'test', $2)"))
        {
            gc.Parameters.AddWithValue(gcId);
            gc.Parameters.AddWithValue(Guid.CreateVersion7());
            await gc.ExecuteNonQueryAsync();
        }

        await using var ev = DataSource.CreateCommand("""
            INSERT INTO gc_event (gc_id, seq, kind, author_kind, author_id, source_trust)
            VALUES ($1, 1, 'message', 'agent', $2, 'agent')
            """);
        ev.Parameters.AddWithValue(gcId);
        ev.Parameters.AddWithValue(Guid.CreateVersion7());
        await ev.ExecuteNonQueryAsync();
        return (gcId, 1);
    }
}
