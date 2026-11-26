using Bazaar.Server.Constellation.Memory;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Constellation.Memory;

/// <summary>
/// The doc id backfill on real PostgreSQL (docs/design/constellation-memory-graph.md, "Convergence design", item 5): the dry
/// run reports and changes nothing; the run maps, keeps every revision, and a second run maps nothing new.
/// </summary>
public sealed class MemoryGraphDocIdMigratorTests(ConstellationDb db) : IClassFixture<ConstellationDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    [Fact]
    public async Task Dry_run_reports_mappings_unmapped_notes_and_conflicts_and_changes_nothing()
    {
        SkipWithoutDatabase();
        var s = await LegacyScenario.InsertAsync(db);
        var before = await SnapshotAsync();

        var report = await new MemoryGraphDocIdMigrator(db.DataSource).DryRunAsync(Ct);

        Assert.True(report.DryRun);
        Assert.Equal(0, report.RowsUpdated);
        var mapped = Assert.Single(report.Mappings, m => m.NoteId == s.Mapped);
        Assert.Equal(s.MappedDoc, mapped.DocId);
        Assert.Equal(2, mapped.Rows);
        Assert.Contains(report.Unmapped, u => u.NoteId == s.Unmapped && u.Reason == MemoryDocIdReasons.NoVaultDoc);
        Assert.Contains(report.Conflicts, c => c.NoteId == s.Mismatch && c.Reason == MemoryDocIdReasons.FrontMatterIdMismatch);
        Assert.Contains(report.Conflicts, c => c.NoteId == s.Ambiguous && c.Reason == MemoryDocIdReasons.AmbiguousPath);
        Assert.DoesNotContain(report.Mappings, m => m.NoteId == s.Unmapped || m.NoteId == s.Mismatch || m.NoteId == s.Ambiguous);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task Run_maps_every_revision_of_a_mapped_note_keeps_all_rows_and_is_idempotent()
    {
        SkipWithoutDatabase();
        var s = await LegacyScenario.InsertAsync(db);
        var migrator = new MemoryGraphDocIdMigrator(db.DataSource);
        var rowsBefore = (await SnapshotAsync()).Count;

        var first = await migrator.RunAsync(Ct);
        var second = await migrator.RunAsync(Ct);

        Assert.False(first.DryRun);
        Assert.Contains(first.Mappings, m => m.NoteId == s.Mapped && m.DocId == s.MappedDoc);
        Assert.True(first.RowsUpdated >= 2);
        Assert.DoesNotContain(second.Mappings, m => m.NoteId == s.Mapped);
        Assert.Equal(0, second.RowsUpdated);
        Assert.True(second.AlreadyMapped >= 1);

        var history = await new PgMemoryGraphStore(db.DataSource).ListRevisionsByDocAsync(s.MappedDoc, Ct);
        Assert.Equal([2L, 1L], history.Select(r => r.Version));
        Assert.Equal(rowsBefore, (await SnapshotAsync()).Count);

        // Unmapped and conflicting notes are left as they were.
        var snapshot = await SnapshotAsync();
        Assert.All(snapshot.Where(r => r.StartsWith(s.Unmapped, StringComparison.Ordinal) || r.StartsWith(s.Mismatch, StringComparison.Ordinal)),
            r => Assert.EndsWith("|", r, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_notes_at_one_doc_are_both_left_unmapped_as_a_conflict()
    {
        SkipWithoutDatabase();
        var g = new GraphSeed(db.DataSource);
        var path = $"agents/agent-a/memory/{g.Tag}-twice.md";
        await g.DocAsync(path, "memory", $"Twice {g.Tag}", owner: "agent-a");
        string first = GraphSeed.NoteId(), second = GraphSeed.NoteId();
        await InsertLegacyRevisionAsync(g, first, 1, path, active: true);
        await InsertLegacyRevisionAsync(g, second, 1, path, active: true);

        var report = await new MemoryGraphDocIdMigrator(db.DataSource).RunAsync(Ct);

        Assert.Contains(report.Conflicts, c => c.NoteId == first && c.Reason == MemoryDocIdReasons.DocClaimedTwice);
        Assert.Contains(report.Conflicts, c => c.NoteId == second && c.Reason == MemoryDocIdReasons.DocClaimedTwice);
        var snapshot = await SnapshotAsync();
        Assert.All(snapshot.Where(r => r.StartsWith(first, StringComparison.Ordinal) || r.StartsWith(second, StringComparison.Ordinal)),
            r => Assert.EndsWith("|", r, StringComparison.Ordinal));
    }

    /// <summary>Every revision row as text (note, version, active, doc id), in a stable order.</summary>
    private async Task<List<string>> SnapshotAsync()
    {
        var rows = new List<string>();
        await using var cmd = db.DataSource.CreateCommand(
            "SELECT note_id, version, is_active, doc_id FROM constellation_memory_revision ORDER BY note_id, version");
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            rows.Add($"{reader.GetString(0)}|{reader.GetInt64(1)}|{reader.GetBoolean(2)}|{(reader.IsDBNull(3) ? "" : reader.GetGuid(3).ToString())}");
        return rows;
    }

    /// <summary>A revision row as constellation/0001 wrote it, with no doc id (data from before the convergence).</summary>
    internal static Task InsertLegacyRevisionAsync(GraphSeed g, string noteId, long version, string path, bool active) =>
        g.ExecAsync(
            "INSERT INTO constellation_memory_revision (note_id, version, scope_agent_id, vault_path, title, kind, status, trust, is_active) VALUES ($1, $2, 'agent-a', $3, 'T', 'Semantic', 'Active', 'AgentVerified', $4)",
            noteId, version, path, active);

    /// <summary>Legacy rows: a mappable note with two revisions, an unmapped one, a front matter mismatch and an ambiguous path.</summary>
    private sealed record LegacyScenario(string Mapped, Guid MappedDoc, string Unmapped, string Mismatch, string Ambiguous)
    {
        public static async Task<LegacyScenario> InsertAsync(ConstellationDb db)
        {
            var g = new GraphSeed(db.DataSource);
            string mapped = GraphSeed.NoteId(), unmapped = GraphSeed.NoteId(), mismatch = GraphSeed.NoteId(), ambiguous = GraphSeed.NoteId();

            var mappedPath = $"agents/agent-a/memory/{g.Tag}-mapped.md";
            var mappedDoc = await g.DocAsync(mappedPath, "memory", $"Mapped {g.Tag}", owner: "agent-a", frontMatterId: mapped);
            await InsertLegacyRevisionAsync(g, mapped, 1, mappedPath, active: false);
            await InsertLegacyRevisionAsync(g, mapped, 2, mappedPath, active: true);

            await InsertLegacyRevisionAsync(g, unmapped, 1, $"agents/agent-a/memory/{g.Tag}-gone.md", active: true);

            var mismatchPath = $"agents/agent-a/memory/{g.Tag}-mismatch.md";
            await g.DocAsync(mismatchPath, "memory", $"Mismatch {g.Tag}", owner: "agent-a", frontMatterId: GraphSeed.NoteId());
            await InsertLegacyRevisionAsync(g, mismatch, 1, mismatchPath, active: true);

            var ambiguousPath = $"agents/agent-a/memory/{g.Tag}-twovaults.md";
            await g.DocAsync(ambiguousPath, "memory", $"Two A {g.Tag}", owner: "agent-a");
            await g.DocAsync(ambiguousPath, "memory", $"Two B {g.Tag}", owner: "agent-a", vaultId: Guid.CreateVersion7());
            await InsertLegacyRevisionAsync(g, ambiguous, 1, ambiguousPath, active: true);

            return new LegacyScenario(mapped, mappedDoc, unmapped, mismatch, ambiguous);
        }
    }
}
