using Bazaar.Server.Constellation.Memory;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Constellation.Memory;

/// <summary>Postgres memory graph store on real PostgreSQL (constellation/0001; docs/design/constellation-memory-graph.md, "Store").</summary>
public sealed class PgMemoryGraphStoreTests(ConstellationDb db) : IClassFixture<ConstellationDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static PgMemoryGraphStore Store(ConstellationDb fixture) => new(fixture.DataSource);

    /// <summary>A fresh note id per test, so tests can share one database.</summary>
    private static string NewNoteId() => "note-" + Guid.CreateVersion7().ToString("N");

    private static MemoryGraphRevision Revision(string noteId, long version, params MemoryGraphEdge[] edges) =>
        new(noteId, version, "agent-a", $"agents/a/memory-{noteId}.md", $"Title v{version}", "Fact", "Active", "Agent", edges);

    private static async Task<long> ScalarAsync(NpgsqlDataSource ds, string sql, params object[] args)
    {
        await using var cmd = ds.CreateCommand(sql);
        foreach (var arg in args)
            cmd.Parameters.AddWithValue(arg);
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task Stored_revision_is_returned_as_the_active_one_with_its_edges()
    {
        SkipWithoutDatabase();
        var store = Store(db);
        var noteId = NewNoteId();
        var revision = Revision(noteId, 1, new MemoryGraphEdge("note-target", MemoryEdgeKind.Link, 1.0));

        await store.StoreRevisionAsync(revision, Ct);

        var active = await store.GetActiveAsync([noteId], Ct);
        var only = Assert.Single(active);
        Assert.Equal(noteId, only.NoteId);
        Assert.Equal(1, only.Version);
        Assert.Equal("agent-a", only.ScopeAgentId);
        Assert.Equal(revision.VaultPath, only.VaultPath);
        Assert.Equal("Title v1", only.Title);
        Assert.Equal("Fact", only.Kind);
        Assert.Equal("Active", only.Status);
        Assert.Equal("Agent", only.Trust);
        Assert.Equal(new[] { new MemoryGraphEdge("note-target", MemoryEdgeKind.Link, 1.0) }, only.Edges);
    }

    [Fact]
    public async Task Storing_a_newer_version_deactivates_the_previous_one_and_keeps_it()
    {
        SkipWithoutDatabase();
        var store = Store(db);
        var noteId = NewNoteId();
        await store.StoreRevisionAsync(Revision(noteId, 1), Ct);
        await store.StoreRevisionAsync(Revision(noteId, 2), Ct);

        var active = Assert.Single(await store.GetActiveAsync([noteId], Ct));
        Assert.Equal(2, active.Version);

        var all = await store.ListRevisionsAsync(noteId, Ct);
        Assert.Equal(new long[] { 2, 1 }, all.Select(r => r.Version).ToArray());
        Assert.Equal(1, await ScalarAsync(db.DataSource,
            "SELECT count(*) FROM constellation_memory_revision WHERE note_id = $1 AND is_active", noteId));
    }

    [Fact]
    public async Task Storing_a_duplicate_note_and_version_fails_and_leaves_the_active_revision_alone()
    {
        SkipWithoutDatabase();
        var store = Store(db);
        var noteId = NewNoteId();
        await store.StoreRevisionAsync(Revision(noteId, 1), Ct);
        await store.StoreRevisionAsync(Revision(noteId, 2), Ct);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.StoreRevisionAsync(Revision(noteId, 2), Ct));
        Assert.Contains("already stored", error.Message);

        var active = Assert.Single(await store.GetActiveAsync([noteId], Ct));
        Assert.Equal(2, active.Version);
        Assert.Equal(2, (await store.ListRevisionsAsync(noteId, Ct)).Count);
    }

    [Fact]
    public async Task Activating_an_earlier_revision_rolls_back_to_it_and_keeps_its_edges()
    {
        SkipWithoutDatabase();
        var store = Store(db);
        var noteId = NewNoteId();
        await store.StoreRevisionAsync(Revision(noteId, 1, new MemoryGraphEdge("first-target", MemoryEdgeKind.DerivedFrom, 0.8)), Ct);
        await store.StoreRevisionAsync(Revision(noteId, 2, new MemoryGraphEdge("second-target", MemoryEdgeKind.Supersedes, 0.5)), Ct);

        Assert.True(await store.ActivateRevisionAsync(noteId, 1, Ct));

        var active = Assert.Single(await store.GetActiveAsync([noteId], Ct));
        Assert.Equal(1, active.Version);
        Assert.Equal(new[] { new MemoryGraphEdge("first-target", MemoryEdgeKind.DerivedFrom, 0.8) }, active.Edges);
        Assert.Equal(2, (await store.ListRevisionsAsync(noteId, Ct)).Count);
        Assert.Equal(1, await ScalarAsync(db.DataSource,
            "SELECT count(*) FROM constellation_memory_revision WHERE note_id = $1 AND is_active", noteId));
    }

    [Fact]
    public async Task Activating_an_unknown_version_returns_false_and_changes_nothing()
    {
        SkipWithoutDatabase();
        var store = Store(db);
        var noteId = NewNoteId();
        await store.StoreRevisionAsync(Revision(noteId, 1), Ct);

        Assert.False(await store.ActivateRevisionAsync(noteId, 9, Ct));
        Assert.False(await store.ActivateRevisionAsync(NewNoteId(), 1, Ct));

        var active = Assert.Single(await store.GetActiveAsync([noteId], Ct));
        Assert.Equal(1, active.Version);
    }

    [Fact]
    public async Task GetActive_with_no_ids_returns_an_empty_list()
    {
        SkipWithoutDatabase();
        var store = Store(db);

        Assert.Empty(await store.GetActiveAsync([], Ct));
    }

    [Fact]
    public async Task GetActive_leaves_out_ids_with_no_active_revision()
    {
        SkipWithoutDatabase();
        var store = Store(db);
        var present = NewNoteId();
        await store.StoreRevisionAsync(Revision(present, 1), Ct);

        var result = await store.GetActiveAsync([present, NewNoteId()], Ct);

        var only = Assert.Single(result);
        Assert.Equal(present, only.NoteId);
    }

    [Fact]
    public async Task Edges_round_trip_with_every_kind_and_weight_in_a_stable_order()
    {
        SkipWithoutDatabase();
        var store = Store(db);
        var noteId = NewNoteId();
        var edges = new[]
        {
            new MemoryGraphEdge("note-z", MemoryEdgeKind.Link, 1.0),
            new MemoryGraphEdge("note-b", MemoryEdgeKind.DerivedFrom, 0.8),
            new MemoryGraphEdge("note-c", MemoryEdgeKind.Supersedes, 0.5),
            new MemoryGraphEdge("agent:a", MemoryEdgeKind.AuthoredBy, 0.0),
            new MemoryGraphEdge("gc:g", MemoryEdgeKind.FromGc, 0.0),
            new MemoryGraphEdge("message:m", MemoryEdgeKind.FromMessage, 0.0),
            new MemoryGraphEdge("note-a", MemoryEdgeKind.Link, 1.0),
        };
        await store.StoreRevisionAsync(Revision(noteId, 1, edges), Ct);

        var active = Assert.Single(await store.GetActiveAsync([noteId], Ct));

        // Kind name first (ordinal), then target id (ordinal).
        Assert.Equal(
            new[]
            {
                new MemoryGraphEdge("agent:a", MemoryEdgeKind.AuthoredBy, 0.0),
                new MemoryGraphEdge("note-b", MemoryEdgeKind.DerivedFrom, 0.8),
                new MemoryGraphEdge("gc:g", MemoryEdgeKind.FromGc, 0.0),
                new MemoryGraphEdge("message:m", MemoryEdgeKind.FromMessage, 0.0),
                new MemoryGraphEdge("note-a", MemoryEdgeKind.Link, 1.0),
                new MemoryGraphEdge("note-z", MemoryEdgeKind.Link, 1.0),
                new MemoryGraphEdge("note-c", MemoryEdgeKind.Supersedes, 0.5),
            },
            active.Edges);
        Assert.Equal(edges.Length, active.Edges.Count);
    }

    [Fact]
    public async Task ListRevisions_returns_every_version_newest_first_and_an_empty_list_for_an_unknown_note()
    {
        SkipWithoutDatabase();
        var store = Store(db);
        var noteId = NewNoteId();
        await store.StoreRevisionAsync(Revision(noteId, 1), Ct);
        await store.StoreRevisionAsync(Revision(noteId, 2), Ct);
        await store.StoreRevisionAsync(Revision(noteId, 3, new MemoryGraphEdge("note-x", MemoryEdgeKind.Link, 1.0)), Ct);

        var all = await store.ListRevisionsAsync(noteId, Ct);

        Assert.Equal(new long[] { 3, 2, 1 }, all.Select(r => r.Version).ToArray());
        Assert.Single(all[0].Edges);
        Assert.Empty(all[1].Edges);
        Assert.Empty(await store.ListRevisionsAsync(NewNoteId(), Ct));
    }

    [Fact]
    public async Task Null_and_malformed_inputs_are_rejected_before_any_query()
    {
        SkipWithoutDatabase();
        var store = Store(db);
        var noteId = NewNoteId();

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.StoreRevisionAsync(null!, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.StoreRevisionAsync(Revision(" ", 1), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.StoreRevisionAsync(Revision(noteId, 1, new MemoryGraphEdge("t", MemoryEdgeKind.Link, 1.5)), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.StoreRevisionAsync(Revision(noteId, 1, new MemoryGraphEdge("t", MemoryEdgeKind.Link, 1.0), new MemoryGraphEdge("t", MemoryEdgeKind.Link, 0.5)), Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.GetActiveAsync(null!, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetActiveAsync([""], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ActivateRevisionAsync("", 1, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ListRevisionsAsync(" ", Ct));

        Assert.Empty(await store.ListRevisionsAsync(noteId, Ct));
    }
}
