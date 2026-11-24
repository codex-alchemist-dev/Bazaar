using Bazaar.Server.Constellation.Memory;
using Bazaar.Server.Host.Startup;
using Bazaar.Testing.Postgres;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Bazaar.Server.Host.Tests;

public sealed class MemoryGraphDb : PgFixture, IAsyncLifetime
{
    public override ValueTask InitializeAsync() => PgSource.IsUnsupportedHost ? ValueTask.CompletedTask : base.InitializeAsync();
}

/// <summary>
/// The operator trigger of the doc id migrator (docs/design/constellation-memory-graph.md, "Convergence design", item 5): a dry run
/// changes nothing, a run needs <c>--confirm</c> and is audited.
/// </summary>
public sealed class MemoryGraphSubcommandTests(MemoryGraphDb db) : IClassFixture<MemoryGraphDb>
{
    private const string _envName = "BAZAAR_MEMORY_GRAPH_TEST_DB";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Bazaar:Host:DataRoot"] = Path.Combine(Path.GetTempPath(), "bazaar-memory-graph-test-" + Guid.NewGuid().ToString("N")),
        ["Bazaar:Host:DatabaseConnectionReference"] = "env:" + _envName,
    }).Build();

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("doc-ids --confirm")]
    [InlineData("doc-ids --run --force")]
    public async Task Bad_arguments_print_the_usage_and_fail(string commandLine)
    {
        var args = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var output = new StringWriter();
        Assert.Equal(1, await MemoryGraphSubcommand.RunAsync(args, output, Config(), Ct));
        Assert.Contains("usage: memory-graph doc-ids", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_without_confirm_is_refused_before_anything_is_read()
    {
        var output = new StringWriter();
        Assert.Equal(1, await MemoryGraphSubcommand.RunAsync(["doc-ids", "--run"], output, new ConfigurationBuilder().Build(), Ct));
        Assert.Contains("--confirm", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dry_run_reports_and_changes_nothing_then_a_confirmed_run_maps_and_is_audited()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        Environment.SetEnvironmentVariable(_envName, db.ConnectionString);
        try
        {
            Assert.Equal(0, await MigrateSubcommand.RunAsync([], new StringWriter(), Config(), Ct));

            // A note stored before the Vault index saw its file: no doc id yet. Then the index catches up.
            await using var ds = NpgsqlDataSource.Create(db.ConnectionString);
            const string path = "agents/agent-a/memory/late.md";
            var noteId = "01JC" + Guid.CreateVersion7().ToString("N").ToUpperInvariant()[..22].Replace('I', '1').Replace('L', '1').Replace('O', '0').Replace('U', 'V');
            await new PgMemoryGraphStore(ds).StoreRevisionAsync(
                new MemoryGraphRevision(noteId, 1, "agent-a", path, "Late", "Semantic", "Active", "AgentVerified", []), Ct);
            var docId = Guid.CreateVersion7();
            await using (var insert = ds.CreateCommand(
                "INSERT INTO vault_docs (doc_id, vault_id, path, title, type, area, owner) VALUES ($1, $2, $3, 'Late', 'memory', 'test', 'agent-a')"))
            {
                insert.Parameters.AddWithValue(docId);
                insert.Parameters.AddWithValue(Guid.CreateVersion7());
                insert.Parameters.AddWithValue(path);
                await insert.ExecuteNonQueryAsync(Ct);
            }

            var dry = new StringWriter();
            Assert.Equal(0, await MemoryGraphSubcommand.RunAsync(["doc-ids"], dry, Config(), Ct));
            Assert.Contains("dry run: 1 to map", dry.ToString(), StringComparison.Ordinal);
            Assert.Contains($"map {noteId} -> {docId:D}", dry.ToString(), StringComparison.Ordinal);
            Assert.Null(await DocIdAsync(ds, noteId));
            Assert.Equal(0, await AuditCountAsync(ds));

            var run = new StringWriter();
            Assert.Equal(0, await MemoryGraphSubcommand.RunAsync(["doc-ids", "--run", "--confirm"], run, Config(), Ct));
            Assert.Contains("run: 1 to map", run.ToString(), StringComparison.Ordinal);
            Assert.Contains("1 rows updated", run.ToString(), StringComparison.Ordinal);
            Assert.Equal(docId, await DocIdAsync(ds, noteId));
            Assert.Equal(1, await AuditCountAsync(ds));

            await using var payload = ds.CreateCommand("SELECT actor, payload::text FROM audit_log WHERE kind = $1");
            payload.Parameters.AddWithValue(MemoryGraphSubcommand.RunAuditKind);
            await using var reader = await payload.ExecuteReaderAsync(Ct);
            Assert.True(await reader.ReadAsync(Ct));
            Assert.Equal(MemoryGraphSubcommand.Actor, reader.GetString(0));
            Assert.Contains(noteId, reader.GetString(1), StringComparison.Ordinal);
            Assert.DoesNotContain(path, reader.GetString(1), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(_envName, null);
        }
    }

    private static async Task<Guid?> DocIdAsync(NpgsqlDataSource ds, string noteId)
    {
        await using var cmd = ds.CreateCommand("SELECT doc_id FROM constellation_memory_revision WHERE note_id = $1");
        cmd.Parameters.AddWithValue(noteId);
        return await cmd.ExecuteScalarAsync(Ct) is Guid id ? id : null;
    }

    private static async Task<long> AuditCountAsync(NpgsqlDataSource ds)
    {
        await using var cmd = ds.CreateCommand("SELECT count(*) FROM audit_log WHERE kind = $1");
        cmd.Parameters.AddWithValue(MemoryGraphSubcommand.RunAuditKind);
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }
}
