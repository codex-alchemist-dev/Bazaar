using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Kanban.Data;

/// <summary>A cloned test database migrated with the core schema (0001) and the Kanban tables (0012).</summary>
public sealed class KanbanDb : PgFixture, IAsyncLifetime
{
    private NpgsqlDataSource? _pool;

    /// <summary>Pooled data source for this class's database, capped at 12 connections.</summary>
    public new NpgsqlDataSource DataSource => _pool ?? throw new InvalidOperationException("KanbanDb is not initialised.");

    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        _pool = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(ConnectionString) { MaxPoolSize = 12 }.ConnectionString);
        await new MigrationRunner(DataSource, new MigrationOptions(16))
            .MigrateAsync(new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly), "linux", CancellationToken.None);
    }

    /// <summary>Creates a GC with one message and returns both ids, for task origins.</summary>
    public async Task<(Guid GcId, Guid MessageId)> CreateGcWithMessageAsync()
    {
        var gcId = Guid.CreateVersion7();
        var messageId = Guid.CreateVersion7();
        await using (var gc = DataSource.CreateCommand("INSERT INTO gc (gc_id, type_key, title, created_by) VALUES ($1, 'general', 'test', $2)"))
        {
            gc.Parameters.AddWithValue(gcId);
            gc.Parameters.AddWithValue(Guid.CreateVersion7());
            await gc.ExecuteNonQueryAsync();
        }

        await using var msg = DataSource.CreateCommand("INSERT INTO gc_message (gc_id, message_id, first_seq, author_id) VALUES ($1, $2, 1, $3)");
        msg.Parameters.AddWithValue(gcId);
        msg.Parameters.AddWithValue(messageId);
        msg.Parameters.AddWithValue(Guid.CreateVersion7());
        await msg.ExecuteNonQueryAsync();
        return (gcId, messageId);
    }
}
