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
        var projectTarget = Guid.NewGuid();
        await index.ReplaceDocumentAsync(Doc(vault, publicTarget, "shared/research/Plan.md"), [Block(0, "plan")], [], Ct);
        await index.ReplaceDocumentAsync(Doc(vault, projectTarget, "projects/x/Plan.md", project: "x"), [Block(0, "project plan")], [], Ct);
        await index.ReplaceDocumentAsync(
            Doc(vault, privateTarget, "agents/alpha/memory/note.md", type: "memory", owner: "agent:alpha"), [Block(0, "note")], [], Ct);
        await index.ReplaceDocumentAsync(
            Doc(vault, seed, "projects/x/Index.md"),
            [Block(0, "see the plan and a note")],
            [
                new VaultLinkRecord(seed, 0, 0, "link", "Plan", publicTarget, null, null),
                new VaultLinkRecord(seed, 0, 1, "link", "note", privateTarget, null, null),
                new VaultLinkRecord(seed, 0, 2, "link", "projects/x/Plan", projectTarget, null, null),
            ],
            Ct);
        var search = new VaultSearchService(new VaultSearchRepository(db.DataSource));

        var forBeta = await search.ExpandNeighboursAsync(vault, [seed], _beta, Ct);
        var forAlpha = await search.ExpandNeighboursAsync(vault, [seed], _alpha, Ct);

        // The project document has no owner; without membership on the caller it is the owner's only (fail closed).
        Assert.Equal([publicTarget], forBeta.Select(card => card.DocId));
        Assert.Equal(new[] { publicTarget, privateTarget }.Order(), forAlpha.Select(card => card.DocId).Order());
        Assert.All(forAlpha, card => Assert.Equal(VaultHitBasis.Extracted, card.Basis));
    }

    // Regression: ownerless documents used to be returned to every caller, so search ignored project membership and Hive grants.
    [Fact]
    public async Task Ownerless_documents_outside_company_and_shared_areas_are_filtered_from_agent_search()
    {
        SkipWithoutDatabase();
        var index = new VaultIndexRepository(db.DataSource);
        var vault = Guid.NewGuid();
        var project = Guid.NewGuid();
        var hive = Guid.NewGuid();
        var decision = Guid.NewGuid();
        var company = Guid.NewGuid();
        var shared = Guid.NewGuid();
        var ownCharter = Guid.NewGuid();
        var otherCharter = Guid.NewGuid();
        await index.ReplaceDocumentAsync(Doc(vault, project, "projects/x/Auth.md", project: "x"), [Block(0, "auth project")], [], Ct);
        await index.ReplaceDocumentAsync(Doc(vault, hive, "hive/procedure/auth.md", type: "memory"), [Block(0, "auth hive")], [], Ct);
        await index.ReplaceDocumentAsync(Doc(vault, decision, "decisions/0001-auth.md", type: "decision"), [Block(0, "auth decision")], [], Ct);
        await index.ReplaceDocumentAsync(Doc(vault, company, "company/Constitution.md", type: "constitution"), [Block(0, "auth company")], [], Ct);
        await index.ReplaceDocumentAsync(Doc(vault, shared, "shared/research/auth.md"), [Block(0, "auth shared")], [], Ct);
        await index.ReplaceDocumentAsync(Doc(vault, ownCharter, "agents/alpha/charter.md", type: "charter"), [Block(0, "auth alpha charter")], [], Ct);
        await index.ReplaceDocumentAsync(Doc(vault, otherCharter, "agents/beta/charter.md", type: "charter"), [Block(0, "auth beta charter")], [], Ct);
        var search = new VaultSearchService(new VaultSearchRepository(db.DataSource));
        var anonymous = new VaultCaller(IsOwner: false, AgentOwner: null);

        var forAlpha = (await search.SearchAsync(new VaultSearchQuery(vault, "auth", _alpha, Limit: 20), Ct)).Cards.Select(c => c.DocId);
        var forOwner = (await search.SearchAsync(new VaultSearchQuery(vault, "auth", _owner, Limit: 20), Ct)).Cards.Select(c => c.DocId);
        var forAnonymous = (await search.SearchAsync(new VaultSearchQuery(vault, "auth", anonymous, Limit: 20), Ct)).Cards;

        Assert.Equal(new[] { company, shared, ownCharter }.Order(), forAlpha.Order());
        Assert.Equal(new[] { project, hive, decision, company, shared, ownCharter, otherCharter }.Order(), forOwner.Order());
        Assert.Empty(forAnonymous);
        Assert.Null(await search.FetchBodyAsync(vault, project, 0, _alpha, Ct));
        Assert.Null(await search.FetchBodyAsync(vault, hive, 0, _alpha, Ct));
        Assert.Equal("auth company", await search.FetchBodyAsync(vault, company, 0, _alpha, Ct));
        Assert.Equal("auth project", await search.FetchBodyAsync(vault, project, 0, _owner, Ct));
    }

    // Project membership: alpha is a member of x, beta of y. Ownerless project areas (projects/<slug>/, twin/<slug>/) open
    // to members only; decisions/, hive/, archive/, drafts/ and root files stay the owner's.
    private static readonly VaultCaller _alphaInX = new(IsOwner: false, AgentOwner: "agent:alpha") { Projects = new HashSet<string> { "x" } };
    private static readonly VaultCaller _betaInY = new(IsOwner: false, AgentOwner: "agent:beta") { Projects = new HashSet<string> { "y" } };

    [Fact]
    public async Task Search_and_body_fetch_filter_project_areas_by_membership()
    {
        SkipWithoutDatabase();
        var index = new VaultIndexRepository(db.DataSource);
        var profile = await NewProfileAsync();
        var vault = Guid.NewGuid();
        var xPlan = Guid.NewGuid();
        var xTwin = Guid.NewGuid();
        var yPlan = Guid.NewGuid();
        var yTwin = Guid.NewGuid();
        var ySemantic = Guid.NewGuid();
        var lookalike = Guid.NewGuid();
        var rootish = Guid.NewGuid();
        var decision = Guid.NewGuid();
        var hive = Guid.NewGuid();
        var archive = Guid.NewGuid();
        var draft = Guid.NewGuid();
        var root = Guid.NewGuid();
        var betaPrivateInX = Guid.NewGuid();
        await index.ReplaceDocumentAsync(Doc(vault, xPlan, "projects/x/Plan.md", project: "x"), [Block(0, "auth x plan")], [], Ct);
        await index.ReplaceDocumentAsync(Doc(vault, xTwin, "twin/x/overview.md", type: "twin"), [Block(0, "auth x twin")], [], Ct);
        await index.ReplaceDocumentAsync(Doc(vault, yPlan, "projects/y/Plan.md", project: "y"), [Block(0, "auth y plan")], [], Ct);
        await index.ReplaceDocumentAsync(Doc(vault, yTwin, "twin/y/overview.md", type: "twin"), [Block(0, "auth y twin")], [], Ct);
        // Front matter claiming project x does not matter: access follows the path.
        await index.ReplaceDocumentAsync(Doc(vault, lookalike, "projects/xy/Plan.md", project: "x"), [Block(0, "auth xy plan")], [], Ct);
        await index.ReplaceDocumentAsync(Doc(vault, rootish, "projects/x.md", project: "x"), [Block(0, "auth projects file")], [], Ct);
        await index.ReplaceDocumentAsync(Doc(vault, decision, "decisions/0001-auth.md", type: "decision", project: "x"), [Block(0, "auth decision")], [], Ct);
        await index.ReplaceDocumentAsync(Doc(vault, hive, "hive/procedure/auth.md", type: "memory"), [Block(0, "auth hive")], [], Ct);
        await index.ReplaceDocumentAsync(Doc(vault, archive, "archive/2026-10.md", type: "archive"), [Block(0, "auth archive")], [], Ct);
        await index.ReplaceDocumentAsync(Doc(vault, draft, "drafts/alpha/auth.md", type: "draft"), [Block(0, "auth draft")], [], Ct);
        await index.ReplaceDocumentAsync(Doc(vault, root, "Index.md", type: "index"), [Block(0, "auth home")], [], Ct);
        await index.ReplaceDocumentAsync(
            Doc(vault, betaPrivateInX, "projects/x/memory/beta.md", type: "memory", owner: "agent:beta"), [Block(0, "auth beta private")], [], Ct);
        // A similarity-only hit in project y: the vector query must apply the same rule.
        await index.ReplaceDocumentAsync(Doc(vault, ySemantic, "projects/y/Zebra.md", project: "y"), [Block(0, "zebra notes")], [], Ct);
        await EmbedAsync(ySemantic, profile, 0, "zebra notes", vector: [1, 0, 0]);
        var search = new VaultSearchService(new VaultSearchRepository(db.DataSource), new AxisEmbedder(profile));
        var noIdentity = new VaultCaller(IsOwner: false, AgentOwner: null) { Projects = new HashSet<string> { "x", "y" } };

        async Task<IEnumerable<Guid>> Hits(VaultCaller caller) =>
            (await search.SearchAsync(new VaultSearchQuery(vault, "auth", caller, Limit: 50), Ct)).Cards.Select(c => c.DocId).Order();

        Assert.Equal(new[] { xPlan, xTwin }.Order(), await Hits(_alphaInX));
        Assert.Equal(new[] { yPlan, yTwin, ySemantic, betaPrivateInX }.Order(), await Hits(_betaInY));
        Assert.Empty(await Hits(_alpha));
        Assert.Empty(await Hits(noIdentity));
        Assert.Equal(
            new[] { xPlan, xTwin, yPlan, yTwin, ySemantic, lookalike, rootish, decision, hive, archive, draft, root, betaPrivateInX }.Order(),
            await Hits(_owner));

        Assert.Equal("auth x plan", await search.FetchBodyAsync(vault, xPlan, 0, _alphaInX, Ct));
        Assert.Null(await search.FetchBodyAsync(vault, yPlan, 0, _alphaInX, Ct));
        Assert.Null(await search.FetchBodyAsync(vault, betaPrivateInX, 0, _alphaInX, Ct));
        Assert.Null(await search.FetchBodyAsync(vault, xPlan, 0, _betaInY, Ct));
        Assert.Null(await search.FetchBodyAsync(vault, xPlan, 0, _alpha, Ct));
        Assert.Equal("auth y plan", await search.FetchBodyAsync(vault, yPlan, 0, _owner, Ct));
    }

    // The SQL access clause and VaultAccess.CanRead must agree on every document and caller (one rule, two places).
    [Fact]
    public async Task Sql_access_matches_the_in_process_rule_for_every_document_and_caller()
    {
        SkipWithoutDatabase();
        var index = new VaultIndexRepository(db.DataSource);
        var vault = Guid.NewGuid();
        (string Path, string? Owner)[] documents =
        [
            ("company/Constitution.md", null), ("shared/research/a.md", null), ("agents/alpha/charter.md", null),
            ("agents/beta/charter.md", null), ("agents/alpha/memory/a.md", "agent:alpha"), ("projects/x/Index.md", null),
            ("projects/x/deep/nested/a.md", null), ("projects/y/Index.md", null), ("projects/xy/Index.md", null),
            ("projects/x.md", null), ("projects/x", null), ("twin/x/a.md", null), ("twin/y/a.md", null), ("twin/x.md", null),
            ("twin/x", null),
            ("projects/x/memory/beta.md", "agent:beta"), ("decisions/0001-a.md", null), ("drafts/alpha/a.md", null),
            ("hive/procedure/a.md", null), ("archive/a.md", null), ("Index.md", null), ("companyish/a.md", null),
        ];
        var ids = new Dictionary<Guid, (string Path, string? Owner)>();
        foreach (var document in documents)
        {
            var id = Guid.NewGuid();
            ids[id] = document;
            await index.ReplaceDocumentAsync(
                Doc(vault, id, document.Path, type: document.Owner is null ? "research" : "memory", owner: document.Owner),
                [Block(0, "auth " + document.Path)], [], Ct);
        }

        var search = new VaultSearchService(new VaultSearchRepository(db.DataSource));
        VaultCaller[] callers =
        [
            _owner, _alpha, _beta, _alphaInX, _betaInY,
            new(IsOwner: false, AgentOwner: "agent:alpha") { Projects = new HashSet<string> { "x", "y" } },
            new(IsOwner: false, AgentOwner: null) { Projects = new HashSet<string> { "x" } },
        ];

        foreach (var caller in callers)
        {
            var expected = ids.Where(pair => VaultAccess.CanRead(pair.Value.Path, pair.Value.Owner, caller)).Select(pair => pair.Key).Order();
            var actual = (await search.SearchAsync(new VaultSearchQuery(vault, "auth", caller, Limit: 50), Ct)).Cards.Select(c => c.DocId).Order();
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public async Task Link_expansion_does_not_leak_a_neighbour_in_a_project_the_caller_is_not_a_member_of()
    {
        SkipWithoutDatabase();
        var index = new VaultIndexRepository(db.DataSource);
        var vault = Guid.NewGuid();
        var seed = Guid.NewGuid();
        var xPlan = Guid.NewGuid();
        var yPlan = Guid.NewGuid();
        var yTwin = Guid.NewGuid();
        var backlinkFromY = Guid.NewGuid();
        await index.ReplaceDocumentAsync(Doc(vault, xPlan, "projects/x/Plan.md", project: "x"), [Block(0, "x plan")], [], Ct);
        await index.ReplaceDocumentAsync(Doc(vault, yPlan, "projects/y/Plan.md", project: "y"), [Block(0, "y plan")], [], Ct);
        await index.ReplaceDocumentAsync(Doc(vault, yTwin, "twin/y/overview.md", type: "twin"), [Block(0, "y twin")], [], Ct);
        await index.ReplaceDocumentAsync(
            Doc(vault, seed, "projects/x/Index.md", project: "x"),
            [Block(0, "links")],
            [
                new VaultLinkRecord(seed, 0, 0, "link", "projects/x/Plan", xPlan, null, null),
                new VaultLinkRecord(seed, 0, 1, "link", "projects/y/Plan", yPlan, null, null),
                new VaultLinkRecord(seed, 0, 2, "link", "twin/y/overview", yTwin, null, null),
            ],
            Ct);
        // A document in project y that links back to the seed must not surface through the inbound direction either.
        await index.ReplaceDocumentAsync(
            Doc(vault, backlinkFromY, "projects/y/Notes.md", project: "y"),
            [Block(0, "back")],
            [new VaultLinkRecord(backlinkFromY, 0, 0, "link", "projects/x/Index", seed, null, null)],
            Ct);
        var search = new VaultSearchService(new VaultSearchRepository(db.DataSource));

        var forAlpha = await search.ExpandNeighboursAsync(vault, [seed], _alphaInX, Ct);
        var forBeta = await search.ExpandNeighboursAsync(vault, [seed], _betaInY, Ct);
        var forOwner = await search.ExpandNeighboursAsync(vault, [seed], _owner, Ct);

        Assert.Equal([xPlan], forAlpha.Select(card => card.DocId));
        Assert.Equal(new[] { yPlan, yTwin, backlinkFromY }.Order(), forBeta.Select(card => card.DocId).Order());
        Assert.Equal(new[] { xPlan, yPlan, yTwin, backlinkFromY }.Order(), forOwner.Select(card => card.DocId).Order());
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
