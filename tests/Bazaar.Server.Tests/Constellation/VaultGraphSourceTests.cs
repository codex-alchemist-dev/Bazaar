using System.Security.Cryptography;
using System.Text;
using Bazaar.Contracts.Graph;
using Bazaar.Server.Constellation.Tiles;
using Bazaar.Server.Tests.Vault.Data;
using Bazaar.Server.Vault.Data;
using Bazaar.Server.Vault.Search;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Constellation;

/// <summary>The pure Vault graph projection: order, dropped links and the version. No database needed.</summary>
public sealed class VaultGraphProjectionTests
{
    private static readonly Guid _a = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid _b = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid _c = Guid.Parse("00000000-0000-0000-0000-00000000000c");

    [Fact]
    public void Nodes_and_edges_come_out_in_a_fixed_order_whatever_the_input_order()
    {
        var one = VaultGraphProjection.Build([_c, _a, _b], [(_b, _a), (_a, _c), (_a, _b)]);
        var two = VaultGraphProjection.Build([_a, _b, _c], [(_a, _b), (_b, _a), (_a, _c)]);

        Assert.Equal(two.Nodes.Select(n => n.Id), one.Nodes.Select(n => n.Id));
        Assert.Equal(two.Edges, one.Edges);
        Assert.Equal(new[] { _a, _b, _c }.Select(id => id.ToString("D")), one.Nodes.Select(n => n.Id));
    }

    [Fact]
    public void Links_to_missing_documents_self_links_and_duplicates_are_dropped()
    {
        var missing = Guid.NewGuid();
        var graph = VaultGraphProjection.Build([_a, _b], [(_a, _b), (_a, _b), (_a, _a), (_a, missing), (missing, _b)]);

        var edge = Assert.Single(graph.Edges);
        Assert.Equal(_a.ToString("D"), edge.From);
        Assert.Equal(_b.ToString("D"), edge.To);
    }

    [Fact]
    public void The_version_is_stable_for_the_same_graph_and_moves_when_a_link_changes()
    {
        var first = VaultGraphProjection.Build([_a, _b, _c], [(_a, _b)]);
        var again = VaultGraphProjection.Build([_c, _b, _a], [(_a, _b)]);
        var changed = VaultGraphProjection.Build([_a, _b, _c], [(_a, _b), (_b, _c)]);

        Assert.Equal(first.GraphVersion, again.GraphVersion);
        Assert.NotEqual(first.GraphVersion, changed.GraphVersion);
        Assert.True(first.GraphVersion >= 0);
    }

    [Fact]
    public void Nodes_carry_the_Vault_kind_and_edges_carry_the_WikiLink_kind()
    {
        var graph = VaultGraphProjection.Build([_a, _b, _c], [(_a, _b), (_b, _c)]);

        Assert.Equal(3, graph.Nodes.Count);
        Assert.All(graph.Nodes, node => Assert.Equal(GraphNodeKind.Vault, node.Kind));
        Assert.Equal(2, graph.Edges.Count);
        Assert.All(graph.Edges, edge => Assert.Equal(GraphEdgeKind.WikiLink, edge.Kind));
    }

    [Fact]
    public void An_empty_vault_is_an_empty_graph()
    {
        var graph = VaultGraphProjection.Build([], []);

        Assert.Empty(graph.Nodes);
        Assert.Empty(graph.Edges);
    }
}

/// <summary>The Vault graph read from real Vault index tables (migration common/0009).</summary>
public sealed class VaultGraphSourceDataTests(VaultDb db) : IClassFixture<VaultDb>
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 8, 6, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static byte[] Hash(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    private static VaultDocRecord Doc(Guid vaultId, Guid docId, string path) =>
        new(docId, vaultId, path, path, "research", null, "shared/research", null, "{}", null, _t0);

    private static VaultBlockRecord Block(string body) =>
        new(0, null, ["Heading"], 1, 2, Hash(body), body);

    private static VaultLinkRecord Link(Guid src, int linkOrd, string targetRaw, Guid? target) =>
        new(src, 0, linkOrd, "link", targetRaw, target, null, null);

    [Fact]
    public async Task The_graph_holds_every_document_and_only_the_resolved_links_between_them()
    {
        SkipWithoutDatabase();
        var repo = new VaultIndexRepository(db.DataSource);
        var vault = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var lone = Guid.NewGuid();
        await repo.ReplaceDocumentAsync(Doc(vault, b, "projects/x/Plan.md"), [Block("plan")], [], Ct);
        await repo.ReplaceDocumentAsync(Doc(vault, lone, "projects/x/Lone.md"), [Block("lone")], [], Ct);
        await repo.ReplaceDocumentAsync(
            Doc(vault, a, "projects/x/Index.md"),
            [Block("see [[Plan]] and [[Missing]]")],
            [Link(a, 0, "Plan", b), Link(a, 1, "Missing", null)],
            Ct);

        var source = new VaultGraphSource(repo, vault);
        var graph = await source.GetSnapshotAsync(Ct);

        Assert.Equal(new[] { a, b, lone }.Select(id => id.ToString("D")).Order(StringComparer.Ordinal),
            graph.Nodes.Select(n => n.Id));
        var edge = Assert.Single(graph.Edges);
        Assert.Equal(a.ToString("D"), edge.From);
        Assert.Equal(b.ToString("D"), edge.To);
        Assert.NotEqual(0, graph.GraphVersion);
    }

    private static VaultDocRecord Memory(Guid vaultId, Guid docId, string path, string owner) =>
        new(docId, vaultId, path, path, "memory", null, "agents/memory", owner, "{}", null, _t0);

    private static readonly GraphViewer _agentA = new(IsOwner: false, AgentOwner: "agent:aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly GraphViewer _agentB = new(IsOwner: false, AgentOwner: "agent:bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly GraphViewer _human = new(IsOwner: false, AgentOwner: null);

    /// <summary>Shared note S links to A's and B's private memory; A's memory links to S; B's memory links to B's second note.</summary>
    private async Task<(Guid Vault, Guid Shared, Guid MemA, Guid MemB, Guid MemB2, VaultIndexRepository Repo)> SeedAsync()
    {
        var repo = new VaultIndexRepository(db.DataSource);
        var vault = Guid.NewGuid();
        var shared = Guid.NewGuid();
        var memA = Guid.NewGuid();
        var memB = Guid.NewGuid();
        var memB2 = Guid.NewGuid();
        await repo.ReplaceDocumentAsync(Memory(vault, memA, "agents/a/memory/one.md", _agentA.AgentOwner!), [Block("a")], [], Ct);
        await repo.ReplaceDocumentAsync(Memory(vault, memB2, "agents/b/memory/two.md", _agentB.AgentOwner!), [Block("b2")], [], Ct);
        await repo.ReplaceDocumentAsync(Memory(vault, memB, "agents/b/memory/one.md", _agentB.AgentOwner!), [Block("b")], [Link(memB, 0, "Two", memB2)], Ct);
        await repo.ReplaceDocumentAsync(
            Doc(vault, shared, "shared/x/Shared.md"),
            [Block("shared")],
            [Link(shared, 0, "A", memA), Link(shared, 1, "B", memB)],
            Ct);
        // The target must exist before a link to it, so A's link back to the shared note is written last.
        await repo.ReplaceDocumentAsync(Memory(vault, memA, "agents/a/memory/one.md", _agentA.AgentOwner!), [Block("a")], [Link(memA, 0, "Shared", shared)], Ct);
        return (vault, shared, memA, memB, memB2, repo);
    }

    private static string[] Ids(GraphSnapshot graph) => graph.Nodes.Select(n => n.Id).ToArray();

    private static string[] Ordered(params Guid[] ids) => ids.Select(id => id.ToString("D")).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task The_owner_sees_every_note_and_link()
    {
        SkipWithoutDatabase();
        var (vault, shared, memA, memB, memB2, repo) = await SeedAsync();

        var graph = await new VaultGraphSource(repo, vault).GetSnapshotAsync(GraphViewer.Owner, Ct);

        Assert.Equal(Ordered(shared, memA, memB, memB2), Ids(graph));
        Assert.Equal(4, graph.Edges.Count);
    }

    [Fact]
    public async Task An_agent_sees_its_own_memory_and_shared_notes_but_none_of_another_agents()
    {
        SkipWithoutDatabase();
        var (vault, shared, memA, _, _, repo) = await SeedAsync();

        var graph = await new VaultGraphSource(repo, vault).GetSnapshotAsync(_agentA, Ct);

        Assert.Equal(Ordered(shared, memA), Ids(graph));
        // Only edges with both ends readable: Shared to A and A to Shared. Nothing points at B's notes.
        Assert.Equal(2, graph.Edges.Count);
        Assert.All(graph.Edges, edge => Assert.Contains(edge.From, Ids(graph)));
        Assert.All(graph.Edges, edge => Assert.Contains(edge.To, Ids(graph)));
    }

    [Fact]
    public async Task A_member_who_is_neither_the_owner_nor_an_agent_sees_nothing_as_in_Vault_search()
    {
        SkipWithoutDatabase();
        var (vault, _, _, _, _, repo) = await SeedAsync();

        var graph = await new VaultGraphSource(repo, vault).GetSnapshotAsync(_human, Ct);

        // The Vault access rule fails closed for a caller with no agent owner (doc 18): the graph shows no more than search does.
        Assert.Empty(graph.Nodes);
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public async Task The_graph_follows_the_same_rule_as_Vault_search_for_every_viewer()
    {
        SkipWithoutDatabase();
        var (vault, shared, memA, memB, memB2, repo) = await SeedAsync();
        var search = new VaultSearchRepository(db.DataSource);

        foreach (var viewer in new[] { GraphViewer.Owner, _agentA, _agentB, _human })
        {
            var caller = new VaultCaller(viewer.IsOwner, viewer.IsOwner ? null : viewer.AgentOwner);
            var graph = await new VaultGraphSource(repo, vault).GetSnapshotAsync(viewer, Ct);

            // The body fetch applies the search clause per document: a node is in the graph exactly when its body is readable.
            foreach (var id in new[] { shared, memA, memB, memB2 })
            {
                var readable = await search.GetBodyAsync(vault, id, 0, caller, Ct) is not null;
                Assert.Equal(readable, Ids(graph).Contains(id.ToString("D")));
            }
        }
    }

    [Fact]
    public async Task A_viewer_with_no_readable_notes_gets_an_empty_graph_that_hides_what_exists()
    {
        SkipWithoutDatabase();
        var repo = new VaultIndexRepository(db.DataSource);
        var vault = Guid.NewGuid();
        var only = Guid.NewGuid();
        await repo.ReplaceDocumentAsync(Memory(vault, only, "agents/b/memory/solo.md", _agentB.AgentOwner!), [Block("solo")], [], Ct);

        var graph = await new VaultGraphSource(repo, vault).GetSnapshotAsync(_agentA, Ct);
        var empty = VaultGraphProjection.Build([], []);

        Assert.Empty(graph.Nodes);
        Assert.Empty(graph.Edges);
        Assert.Equal(empty.GraphVersion, graph.GraphVersion);
    }

    [Fact]
    public async Task Another_vault_is_not_in_the_graph()
    {
        SkipWithoutDatabase();
        var repo = new VaultIndexRepository(db.DataSource);
        var mine = Guid.NewGuid();
        var other = Guid.NewGuid();
        var doc = Guid.NewGuid();
        await repo.ReplaceDocumentAsync(Doc(other, doc, "elsewhere/Doc.md"), [Block("elsewhere")], [], Ct);

        var graph = await new VaultGraphSource(repo, mine).GetSnapshotAsync(Ct);

        Assert.Empty(graph.Nodes);
        Assert.Empty(graph.Edges);
    }
}
