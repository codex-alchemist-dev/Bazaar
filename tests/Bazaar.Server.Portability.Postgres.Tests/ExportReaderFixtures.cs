using Bazaar.Contracts.Gc;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Gc.Read;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Portability.Postgres.Tests;

/// <summary>A test database migrated with the real core schema (<c>common/*</c>), plus raw seeding helpers for the export readers.</summary>
internal sealed class ExportReaderDb : IAsyncDisposable
{
    private readonly PgTestDatabase _db;

    private ExportReaderDb(PgTestDatabase db) => _db = db;

    public NpgsqlDataSource DataSource => _db.DataSource;

    /// <summary>Opens a migrated database, or skips the test on a host with no test PostgreSQL.</summary>
    public static async Task<ExportReaderDb> OpenAsync(CancellationToken ct)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var db = await PgTestDatabase.CreateAsync(
            "export",
            async (ds, token) => await new MigrationRunner(ds, new MigrationOptions(16)).MigrateAsync(
                new CoreSubsetSource(new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly), new EmbeddedMigrationSource(typeof(PostgresImportJobStore).Assembly)), MigrateCommand.CurrentProvider(), token),
            ct);
        return new ExportReaderDb(db);
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    public async Task<Guid> CreateGcAsync()
    {
        var id = Guid.CreateVersion7();
        await ExecAsync("INSERT INTO gc (gc_id, type_key, title, created_by) VALUES ($1, 'general', 'test', $1)", id);
        return id;
    }

    public async Task ExecAsync(string sql, params object?[] args)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        foreach (var a in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = a ?? DBNull.Value });
        }

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Writes a message with one version per entry; the version number is the 1-based position and seq comes from <paramref name="seqs"/>.</summary>
    public async Task AddMessageAsync(Guid gcId, Guid messageId, Guid author, long[] seqs, bool lastIsTombstone = false)
    {
        await ExecAsync("INSERT INTO gc_message (gc_id, message_id, first_seq, author_id) VALUES ($1, $2, $3, $4)", gcId, messageId, seqs[0], author);
        for (var i = 0; i < seqs.Length; i++)
        {
            var tombstone = lastIsTombstone && i == seqs.Length - 1;
            await ExecAsync(
                "INSERT INTO gc_message_version (gc_id, message_id, version, seq, body_ct, body_nonce, key_id, editor_id, tombstone, created_at) " +
                "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)",
                gcId, messageId, i + 1, seqs[i],
                tombstone ? null : new byte[] { 1, 2, (byte)seqs[i] },
                tombstone ? null : new byte[] { 9, 9 },
                tombstone ? null : Guid.Parse("11111111-1111-1111-1111-111111111111"),
                author, tombstone, new DateTimeOffset(2026, 11, 20, 8, 0, (int)seqs[i], TimeSpan.Zero));
        }
    }
}

/// <summary>
/// Only the core migrations the readers read from: <c>common/0001</c> (gc tables), <c>common/0012</c> (tasks) and <c>kanban/0001</c> and <c>0002</c> (gate table, import link). The rest of the core
/// schema needs extensions (pgvector) these tests do not use, and the readers depend on nothing else.
/// </summary>
internal sealed class CoreSubsetSource(IMigrationSource inner, IMigrationSource portability) : IMigrationSource
{
    // kanban/0001 and 0002 add the gate table and the import link on tasks.
    public IReadOnlyList<string> Namespaces() => ["common", "kanban", "portability"];

    public IReadOnlyList<MigrationFile> Load(string ns) => ns switch
    {
        "common" => [.. inner.Load(ns).Where(f => f.Version is 1 or 12)],
        "kanban" => [.. inner.Load(ns).Where(f => f.Version is 1 or 2)],
        "portability" => portability.Load(ns),
        _ => [],
    };
}

/// <summary>A grant source with one fixed answer.</summary>
internal sealed class FixedGrantAccess(GcReadGrant grant) : IGcReadAccess
{
    public ValueTask<GcReadGrant> GetGrantAsync(Guid gcId, GcPrincipal viewer, CancellationToken ct) => ValueTask.FromResult(grant);
}

internal static class ExportReaderPeople
{
    public static readonly GcPrincipal Viewer = new(Guid.Parse("22222222-2222-2222-2222-222222222222"), PrincipalKind.Owner, "owner");
}
