using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Tests.Vault.Data;
using Bazaar.Server.Vault.Data;
using Bazaar.Server.Vault.Search;
using Npgsql;

namespace Bazaar.Server.Tests.Vault.Search;

/// <summary>
/// Hybrid search, neighbour expansion and body fetch against real Postgres (migration common/0009). The fake embedder
/// maps the word "auth" to one axis and anything else to another, so vector hits are predictable.
/// </summary>
public sealed class VaultSearchDataTests(VaultDb db) : IClassFixture<VaultDb>
{
    private static readonly DateTimeOffset _t0 = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly VaultCaller _owner = VaultCaller.Owner;
    private static readonly VaultCaller _alpha = new(IsOwner: false, AgentOwner: "agent:alpha");
    private static readonly VaultCaller _beta = new(IsOwner: false, AgentOwner: "agent:beta");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(Bazaar.Testing.Postgres.PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static byte[] Hash(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    private static VaultBlockRecord Block(int ordinal, string body) =>
        new(ordinal, null, ["Heading"], ordinal * 2 + 1, ordinal * 2 + 2, Hash(body), body);

    private static VaultDocRecord Doc(Guid vaultId, Guid docId, string path, string type = "research", string? owner = null, string? project = null) =>
        new(docId, vaultId, path, path, type, project, owner is null ? "shared/research" : "agents/alpha/memory", owner, "{}", null, _t0);

    private static float[] Axis(string text) => text.Contains("auth", StringComparison.OrdinalIgnoreCase) ? [1, 0, 0] : [0, 0, 1];

    private async Task<Guid> NewProfileAsync()
    {
        await using var cmd = db.DataSource.CreateCommand(
            "INSERT INTO embedding_profile (provider_id, model_id, dimensions) VALUES ('test', 'fake-axis', 3) RETURNING profile_id");
        return (Guid)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private async Task EmbedAsync(Guid docId, Guid profileId, int ordinal, string body, float[]? vector = null)
    {
        var embeddings = new VaultEmbeddingRepository(db.DataSource);
        await embeddings.UpsertAsync(docId, profileId, [new VaultBlockEmbedding(ordinal, Hash(body), vector ?? Axis(body))], Ct);
    }

    private sealed class AxisEmbedder(Guid profileId) : IVaultQueryEmbedder
    {
        public Guid ProfileId { get; } = profileId;

        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken) => Task.FromResult(Axis(text));
    }

    [Fact]
    public async Task Lexical_search_returns_a_card_with_extracted_basis_and_says_it_is_lexical_only()
    {
        SkipWithoutDatabase();
        var index = new VaultIndexRepository(db.DataSource);
        var vault = Guid.NewGuid();
        var auth = Guid.NewGuid();
        var billing = Guid.NewGuid();
        await index.ReplaceDocumentAsync(Doc(vault, auth, "projects/x/Auth.md", project: "x"), [Block(0, "Token rotation for auth.")], [], Ct);
        await index.ReplaceDocumentAsync(Doc(vault, billing, "projects/x/Billing.md", project: "x"), [Block(0, "Invoices and refunds.")], [], Ct);
        var search = new VaultSearchService(new VaultSearchRepository(db.DataSource));

        var result = await search.SearchAsync(new VaultSearchQuery(vault, "auth", _owner), Ct);

        var card = Assert.Single(result.Cards);
        Assert.Equal(auth, card.DocId);
        Assert.Equal(VaultHitBasis.Extracted, card.Basis);
        Assert.Equal("Token rotation for auth.", card.Gist);
        Assert.True(result.LexicalOnly);
    }

    [Fact]
    public async Task Private_memory_is_absent_for_other_agents_and_visible_to_its_agent_and_the_owner()
    {
        SkipWithoutDatabase();
        var index = new VaultIndexRepository(db.DataSource);
        var vault = Guid.NewGuid();
        var memory = Guid.NewGuid();
        await index.ReplaceDocumentAsync(
            Doc(vault, memory, "agents/alpha/memory/plan.md", type: "memory", owner: "agent:alpha"),
            [Block(0, "Alpha's private auth plan.")], [], Ct);
        var search = new VaultSearchService(new VaultSearchRepository(db.DataSource));

        Assert.Empty((await search.SearchAsync(new VaultSearchQuery(vault, "auth", _beta), Ct)).Cards);
        Assert.Equal(memory, (await search.SearchAsync(new VaultSearchQuery(vault, "auth", _alpha), Ct)).Cards.Single().DocId);
        Assert.Equal(memory, (await search.SearchAsync(new VaultSearchQuery(vault, "auth", _owner), Ct)).Cards.Single().DocId);
    }

    [Fact]
    public async Task Project_and_type_filters_apply_before_ranking()
    {
        SkipWithoutDatabase();
        var index = new VaultIndexRepository(db.DataSource);
        var vault = Guid.NewGuid();
        var inProject = Guid.NewGuid();
        var otherProject = Guid.NewGuid();
        await index.ReplaceDocumentAsync(Doc(vault, inProject, "projects/x/A.md", project: "x"), [Block(0, "auth in x")], [], Ct);
        await index.ReplaceDocumentAsync(Doc(vault, otherProject, "projects/y/A.md", project: "y", type: "plan"), [Block(0, "auth in y")], [], Ct);
        var search = new VaultSearchService(new VaultSearchRepository(db.DataSource));

        var byProject = await search.SearchAsync(new VaultSearchQuery(vault, "auth", _owner, Project: "y"), Ct);
        var byType = await search.SearchAsync(new VaultSearchQuery(vault, "auth", _owner, Type: "research"), Ct);

        Assert.Equal(otherProject, Assert.Single(byProject.Cards).DocId);
        Assert.Equal(inProject, Assert.Single(byType.Cards).DocId);
    }

    [Fact]
    public async Task Hybrid_search_labels_a_similarity_only_hit_as_inferred_and_collapses_to_documents()
    {
        SkipWithoutDatabase();
        var index = new VaultIndexRepository(db.DataSource);
        var profile = await NewProfileAsync();
        var vault = Guid.NewGuid();
        var lexicalDoc = Guid.NewGuid();
        var semanticDoc = Guid.NewGuid();
        await index.ReplaceDocumentAsync(Doc(vault, lexicalDoc, "projects/x/Auth.md"), [Block(0, "auth flow"), Block(1, "auth again")], [], Ct);
        await EmbedAsync(lexicalDoc, profile, 0, "auth flow");
        await EmbedAsync(lexicalDoc, profile, 1, "auth again");
        // The semantic document never says "auth", but its vector sits on the auth axis.
        await index.ReplaceDocumentAsync(Doc(vault, semanticDoc, "projects/x/Zebra.md"), [Block(0, "zebra notes")], [], Ct);
        await EmbedAsync(semanticDoc, profile, 0, "zebra notes", vector: [1, 0, 0]);
        var search = new VaultSearchService(new VaultSearchRepository(db.DataSource), new AxisEmbedder(profile));

        var result = await search.SearchAsync(new VaultSearchQuery(vault, "auth", _owner), Ct);

        Assert.False(result.LexicalOnly);
        Assert.Equal([lexicalDoc, semanticDoc], result.Cards.Select(card => card.DocId));
        Assert.Equal([VaultHitBasis.Extracted, VaultHitBasis.Inferred], result.Cards.Select(card => card.Basis));
        Assert.Single(result.Cards, card => card.DocId == lexicalDoc);
    }

    [Fact]
    public async Task Link_expansion_returns_readable_neighbours_only_and_labels_them_extracted()
    {
        SkipWithoutDatabase();
        var index = new VaultIndexRepository(db.DataSource);
        var vault = Guid.NewGuid();
        var seed = Guid.NewGuid();
        var publicTarget = Guid.NewGuid();
        var privateTarget = Guid.NewGuid();
        await index.ReplaceDocumentAsync(Doc(vault, publicTarget, "projects/x/Plan.md"), [Block(0, "plan")], [], Ct);
        await index.ReplaceDocumentAsync(
            Doc(vault, privateTarget, "agents/alpha/memory/note.md", type: "memory", owner: "agent:alpha"), [Block(0, "note")], [], Ct);
        await index.ReplaceDocumentAsync(
            Doc(vault, seed, "projects/x/Index.md"),
            [Block(0, "see the plan and a note")],
            [
                new VaultLinkRecord(seed, 0, 0, "link", "Plan", publicTarget, null, null),
                new VaultLinkRecord(seed, 0, 1, "link", "note", privateTarget, null, null),
            ],
            Ct);
        var search = new VaultSearchService(new VaultSearchRepository(db.DataSource));

        var forBeta = await search.ExpandNeighboursAsync(vault, [seed], _beta, Ct);
        var forAlpha = await search.ExpandNeighboursAsync(vault, [seed], _alpha, Ct);

        Assert.Equal([publicTarget], forBeta.Select(card => card.DocId));
        Assert.Equal(new[] { publicTarget, privateTarget }.Order(), forAlpha.Select(card => card.DocId).Order());
        Assert.All(forAlpha, card => Assert.Equal(VaultHitBasis.Extracted, card.Basis));
    }

    [Fact]
    public async Task Body_fetch_by_id_respects_access_and_returns_null_for_absent_blocks()
    {
        SkipWithoutDatabase();
        var index = new VaultIndexRepository(db.DataSource);
        var vault = Guid.NewGuid();
        var memory = Guid.NewGuid();
        await index.ReplaceDocumentAsync(
            Doc(vault, memory, "agents/alpha/memory/plan.md", type: "memory", owner: "agent:alpha"),
            [Block(0, "private body")], [], Ct);
        var search = new VaultSearchService(new VaultSearchRepository(db.DataSource));

        Assert.Null(await search.FetchBodyAsync(vault, memory, 0, _beta, Ct));
        Assert.Equal("private body", await search.FetchBodyAsync(vault, memory, 0, _alpha, Ct));
        Assert.Equal("private body", await search.FetchBodyAsync(vault, memory, 0, _owner, Ct));
        Assert.Null(await search.FetchBodyAsync(vault, memory, 9, _owner, Ct));
    }

    [Fact]
    public async Task Search_reports_the_indexed_commit_so_callers_can_see_staleness()
    {
        SkipWithoutDatabase();
        var index = new VaultIndexRepository(db.DataSource);
        var vault = Guid.NewGuid();
        await index.ReplaceDocumentAsync(Doc(vault, Guid.NewGuid(), "projects/x/Auth.md"), [Block(0, "auth")], [], Ct);
        const string commit = "4444444444444444444444444444444444444444";
        await using (var cmd = db.DataSource.CreateCommand("INSERT INTO vault_state (vault_id, indexed_commit) VALUES ($1, $2)"))
        {
            cmd.Parameters.AddWithValue(vault);
            cmd.Parameters.AddWithValue(commit);
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        var search = new VaultSearchService(new VaultSearchRepository(db.DataSource));

        var result = await search.SearchAsync(new VaultSearchQuery(vault, "auth", _owner), Ct);

        Assert.Equal(commit, result.IndexedCommit);
    }
}
