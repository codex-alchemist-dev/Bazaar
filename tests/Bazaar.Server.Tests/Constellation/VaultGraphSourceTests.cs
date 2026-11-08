using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Constellation.Tiles;
using Bazaar.Server.Tests.Vault.Data;
using Bazaar.Server.Vault.Data;
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
