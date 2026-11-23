using Bazaar.Server.Constellation.Memory;
using Bazaar.Server.Memory.Access;
using Bazaar.Server.Memory.Retrieval;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Constellation.Memory;

/// <summary>
/// The converged graph on real PostgreSQL (docs/design/constellation-memory-graph.md, "Convergence design"): memory notes are
/// Vault doc nodes, links are resolved <c>vault_links</c>, and retrieval expands over doc ids with the scope filter on every hop.
/// </summary>
public sealed class ConvergedGraphTests(ConstellationDb db) : IClassFixture<ConstellationDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    [Fact]
    public async Task Store_maps_a_note_to_its_vault_doc_by_path()
    {
        SkipWithoutDatabase();
        var g = new GraphSeed(db.DataSource);
        var docId = await g.DocAsync($"agents/agent-a/memory/{g.Tag}-a.md", "memory", $"A {g.Tag}", owner: "agent-a");
        var noteId = GraphSeed.NoteId();

        await Store().StoreRevisionAsync(Revision(noteId, 1, "agent-a", $"agents/agent-a/memory/{g.Tag}-a.md"), Ct);

        var active = Assert.Single(await Store().GetActiveAsync([noteId], Ct));
        Assert.Equal(docId, active.DocId);
        var history = await Store().ListRevisionsByDocAsync(docId, Ct);
        Assert.Equal([1L], history.Select(r => r.Version));
    }

    [Fact]
    public async Task A_note_stored_before_the_index_saw_it_gets_its_doc_id_on_the_next_write_for_its_whole_history()
    {
        SkipWithoutDatabase();
        var g = new GraphSeed(db.DataSource);
        var path = $"agents/agent-a/memory/{g.Tag}-late.md";
        var noteId = GraphSeed.NoteId();
        await Store().StoreRevisionAsync(Revision(noteId, 1, "agent-a", path), Ct);
        Assert.Null(Assert.Single(await Store().GetActiveAsync([noteId], Ct)).DocId);

        var docId = await g.DocAsync(path, "memory", $"Late {g.Tag}", owner: "agent-a");
        await Store().StoreRevisionAsync(Revision(noteId, 2, "agent-a", path), Ct);

        var history = await Store().ListRevisionsByDocAsync(docId, Ct);
        Assert.Equal([2L, 1L], history.Select(r => r.Version));
        Assert.All(history, r => Assert.Equal(docId, r.DocId));
    }

    [Fact]
    public async Task A_doc_id_once_set_cannot_be_changed()
    {
        SkipWithoutDatabase();
        var g = new GraphSeed(db.DataSource);
        var path = $"agents/agent-a/memory/{g.Tag}-fixed.md";
        await g.DocAsync(path, "memory", $"Fixed {g.Tag}", owner: "agent-a");
        var noteId = GraphSeed.NoteId();
        await Store().StoreRevisionAsync(Revision(noteId, 1, "agent-a", path), Ct);

        await using var cmd = db.DataSource.CreateCommand("UPDATE constellation_memory_revision SET doc_id = $1 WHERE note_id = $2");
        cmd.Parameters.AddWithValue(Guid.CreateVersion7());
        cmd.Parameters.AddWithValue(noteId);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(Ct));
        Assert.Equal("23514", ex.SqlState);
    }

    [Fact]
    public async Task A_second_note_cannot_become_the_active_memory_of_a_doc_that_already_has_one()
    {
        SkipWithoutDatabase();
        var g = new GraphSeed(db.DataSource);
        var path = $"agents/agent-a/memory/{g.Tag}-shared.md";
        var docId = await g.DocAsync(path, "memory", $"Shared {g.Tag}", owner: "agent-a");
        await Store().StoreRevisionAsync(Revision(GraphSeed.NoteId(), 1, "agent-a", path), Ct);

        var second = Revision(GraphSeed.NoteId(), 1, "agent-a", $"agents/agent-a/memory/{g.Tag}-other.md") with { DocId = docId };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().StoreRevisionAsync(second, Ct));
    }

    [Fact]
    public async Task Doc_index_returns_the_resolved_links_and_refuses_a_front_matter_id_of_another_note()
    {
        SkipWithoutDatabase();
        var g = new GraphSeed(db.DataSource);
        var noteId = GraphSeed.NoteId();
        var path = $"agents/agent-a/memory/{g.Tag}-idx.md";
        var note = await g.DocAsync(path, "memory", $"Idx {g.Tag}", owner: "agent-a", frontMatterId: noteId);
        var decision = await g.DocAsync($"projects/p{g.Tag}/decisions/d.md", "decision", $"Decision {g.Tag}");
        await g.LinkAsync(note, $"Decision {g.Tag}", decision);
        await g.LinkAsync(note, "Nowhere", null);
        var index = new PgVaultDocIndex(db.DataSource);

        var found = await index.FindAsync(noteId, path, Ct);
        var other = await index.FindAsync(GraphSeed.NoteId(), path, Ct);

        Assert.NotNull(found);
        Assert.Equal(note, found.DocId);
        Assert.Equal([decision], found.LinkTargets);
        Assert.Null(other);
    }

    [Fact]
    public async Task Retrieval_hops_from_a_memory_note_to_a_project_decision_and_a_linked_project_note_but_never_to_hidden_nodes()
    {
        SkipWithoutDatabase();
        var g = new GraphSeed(db.DataSource);
        string home = "p" + g.Tag, linked = "q" + g.Tag, foreign = "r" + g.Tag;

        // Agent A's memory note links by title to a decision in its project, to a doc in a linked project it is also a member
        // of, to a doc in a project it is not a member of, and to agent B's private note (which links on to a public decision).
        var memoryPath = $"agents/agent-a/memory/{g.Tag}-seed.md";
        var memory = await g.DocAsync(memoryPath, "memory", $"Seed {g.Tag}", owner: "agent-a");
        var decision = await g.DocAsync($"projects/{home}/decisions/retry.md", "decision", $"Retry decision {g.Tag}", status: "accepted");
        var linkedNote = await g.DocAsync($"projects/{linked}/research/cache.md", "research", $"Cache research {g.Tag}");
        var foreignDoc = await g.DocAsync($"projects/{foreign}/plans/p.md", "plan", $"Foreign plan {g.Tag}");
        var secretPath = $"agents/agent-b/memory/{g.Tag}-secret.md";
        var secret = await g.DocAsync(secretPath, "memory", $"Secret {g.Tag}", owner: "agent-b");
        var behindSecret = await g.DocAsync($"projects/{home}/decisions/behind.md", "decision", $"Behind {g.Tag}", status: "accepted");
        await g.LinkAsync(memory, $"Retry decision {g.Tag}", decision);
        await g.LinkAsync(memory, $"Cache research {g.Tag}", linkedNote);
        await g.LinkAsync(memory, $"Foreign plan {g.Tag}", foreignDoc);
        await g.LinkAsync(memory, $"Secret {g.Tag}", secret);
        await g.LinkAsync(secret, $"Behind {g.Tag}", behindSecret);

        var store = Store();
        var seedNote = GraphSeed.NoteId();
        var secretNote = GraphSeed.NoteId();
        await store.StoreRevisionAsync(Revision(seedNote, 1, "agent-a", memoryPath), Ct);
        await store.StoreRevisionAsync(Revision(secretNote, 1, "agent-b", secretPath), Ct);

        var access = new VaultPathAccess(new StaticProjectMembership(new Dictionary<string, ProjectMembers>
        {
            [home] = new(new HashSet<string> { "agent-a" }, new HashSet<string>()),
            [linked] = new(new HashSet<string> { "agent-a" }, new HashSet<string>()),
            [foreign] = new(new HashSet<string> { "agent-b" }, new HashSet<string>()),
        }));
        var retriever = new MemoryGraphRetriever(new PgConstellationNodeReader(db.DataSource), access);

        var result = await Retrieve(retriever, VaultPrincipal.Agent("agent-a"), [memory], maxHops: 2,
            memory, decision, linkedNote, foreignDoc, secret, behindSecret);

        Assert.Equal(
            new[] { memory, decision, linkedNote }.Select(Id).Order(StringComparer.Ordinal),
            result.Hits.Select(h => h.DocId).Order(StringComparer.Ordinal));
        var seed = result.Hits.Single(h => h.DocId == Id(memory));
        Assert.Equal(seedNote, seed.NoteId);
        Assert.Equal("agent-a", seed.ScopeAgentId);
        Assert.Equal(0, seed.HopDistance);
        var hop = result.Hits.Single(h => h.DocId == Id(decision));
        Assert.Null(hop.NoteId);
        Assert.Equal("decision", hop.Kind);
        Assert.Equal(1, hop.HopDistance);
        Assert.Equal(seed.Score * 1.0 * 0.5, hop.Score, 12);

        // The owner reads all, so the same graph shows the hidden nodes to the owner, including the hop beyond the secret note.
        var owner = await Retrieve(retriever, VaultPrincipal.Owner(), [memory], maxHops: 2,
            memory, decision, linkedNote, foreignDoc, secret, behindSecret);
        Assert.Contains(owner.Hits, h => h.DocId == Id(secret) && h.NoteId == secretNote);
        Assert.Contains(owner.Hits, h => h.DocId == Id(behindSecret) && h.HopDistance == 2);
    }

    [Fact]
    public async Task Retrieval_follows_derived_from_note_ids_mapped_to_docs_and_drops_deprecated_nodes()
    {
        SkipWithoutDatabase();
        var g = new GraphSeed(db.DataSource);
        var basePath = $"agents/agent-a/memory/{g.Tag}-base.md";
        var derivedPath = $"agents/agent-a/memory/{g.Tag}-derived.md";
        var oldPath = $"agents/agent-a/memory/{g.Tag}-old.md";
        var baseDoc = await g.DocAsync(basePath, "memory", $"Base {g.Tag}", owner: "agent-a");
        var derivedDoc = await g.DocAsync(derivedPath, "memory", $"Derived {g.Tag}", owner: "agent-a");
        var oldDoc = await g.DocAsync(oldPath, "memory", $"Old {g.Tag}", owner: "agent-a");
        var staleDoc = await g.DocAsync($"projects/p{g.Tag}/plans/stale.md", "plan", $"Stale {g.Tag}", status: "deprecated");
        await g.LinkAsync(derivedDoc, $"Old {g.Tag}", oldDoc);
        await g.LinkAsync(derivedDoc, $"Stale {g.Tag}", staleDoc);

        var baseNote = GraphSeed.NoteId();
        var store = Store();
        await store.StoreRevisionAsync(Revision(baseNote, 1, "agent-a", basePath), Ct);
        await store.StoreRevisionAsync(
            Revision(GraphSeed.NoteId(), 1, "agent-a", derivedPath, new MemoryGraphEdge(baseNote, MemoryEdgeKind.DerivedFrom, 0.8)), Ct);
        await store.StoreRevisionAsync(Revision(GraphSeed.NoteId(), 1, "agent-a", oldPath) with { Status = "Deprecated" }, Ct);

        var access = new VaultPathAccess(new StaticProjectMembership(new Dictionary<string, ProjectMembers>
        {
            ["p" + g.Tag] = new(new HashSet<string> { "agent-a" }, new HashSet<string>()),
        }));
        var retriever = new MemoryGraphRetriever(new PgConstellationNodeReader(db.DataSource), access);

        var result = await Retrieve(retriever, VaultPrincipal.Agent("agent-a"), [derivedDoc], maxHops: 1,
            baseDoc, derivedDoc, oldDoc, staleDoc);

        Assert.Equal([Id(derivedDoc), Id(baseDoc)], result.Hits.Select(h => h.DocId));
        Assert.Equal(baseNote, result.Hits[1].NoteId);
    }

    private PgMemoryGraphStore Store() => new(db.DataSource);

    private static string Id(Guid docId) => docId.ToString("D");

    private static MemoryGraphRevision Revision(string noteId, long version, string agent, string path, params MemoryGraphEdge[] edges) =>
        new(noteId, version, agent, path, $"Title v{version}", "Semantic", "Active", "AgentVerified", edges);

    private static Task<MemoryGraphRetrieval> Retrieve(
        MemoryGraphRetriever retriever,
        VaultPrincipal who,
        Guid[] seeds,
        int maxHops,
        params Guid[] all) =>
        retriever.RetrieveAsync(
            who,
            seeds.Select((id, i) => new RankedHit(Id(id), seeds.Length - i)).ToArray(),
            [],
            all.ToDictionary(Id, _ => 1, StringComparer.Ordinal),
            tokenBudget: 1000,
            new MemoryGraphOptions(MaxHops: maxHops),
            Ct);
}

/// <summary>Vault index rows for one test: docs with one block each, and links between them. Names carry a fresh tag.</summary>
internal sealed class GraphSeed(NpgsqlDataSource ds)
{
    private readonly Dictionary<Guid, int> _linkCount = [];

    public string Tag { get; } = Guid.CreateVersion7().ToString("N")[..12];

    public Guid VaultId { get; } = Guid.CreateVersion7();

    /// <summary>A fresh 26-character ULID-shaped note id.</summary>
    public static string NoteId() => ("01" + Guid.CreateVersion7().ToString("N").ToUpperInvariant())[..26]
        .Replace('I', '1').Replace('L', '1').Replace('O', '0').Replace('U', 'V');

    public async Task<Guid> DocAsync(string path, string type, string title, string? owner = null, string? status = null, string? frontMatterId = null, Guid? vaultId = null)
    {
        var docId = Guid.CreateVersion7();
        var frontMatter = new System.Text.Json.Nodes.JsonObject();
        if (status is not null)
            frontMatter["status"] = status;
        if (frontMatterId is not null)
            frontMatter["id"] = frontMatterId;
        await ExecAsync(
            "INSERT INTO vault_docs (doc_id, vault_id, path, title, type, area, owner, frontmatter) VALUES ($1, $2, $3, $4, $5, 'test', $6, $7::jsonb)",
            docId, vaultId ?? VaultId, path, title, type, (object?)owner ?? DBNull.Value, frontMatter.ToJsonString());
        await ExecAsync(
            "INSERT INTO vault_blocks (doc_id, ordinal, start_line, end_line, text_hash, body) VALUES ($1, 0, 1, 1, $2, 'body')",
            docId, new byte[32]);
        return docId;
    }

    public async Task LinkAsync(Guid source, string targetRaw, Guid? target)
    {
        var ordinal = _linkCount.TryGetValue(source, out var n) ? n : 0;
        _linkCount[source] = ordinal + 1;
        await ExecAsync(
            "INSERT INTO vault_links (src_doc, src_ordinal, link_ord, kind, target_raw, target_doc) VALUES ($1, 0, $2, 'link', $3, $4)",
            source, ordinal, targetRaw, (object?)target ?? DBNull.Value);
    }

    public async Task ExecAsync(string sql, params object[] args)
    {
        await using var cmd = ds.CreateCommand(sql);
        foreach (var arg in args)
            cmd.Parameters.Add(arg is DBNull ? new NpgsqlParameter { Value = DBNull.Value } : new NpgsqlParameter { Value = arg });
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
