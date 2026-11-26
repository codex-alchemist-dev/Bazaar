using Bazaar.Server.Constellation.Memory;
using Bazaar.Server.Memory.Access;
using Bazaar.Server.Memory.Retrieval;
using Bazaar.Server.Vault.Search;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Constellation.Memory;

/// <summary>
/// Vector seeds on real PostgreSQL with a fake query embedder (docs/design/constellation-memory-graph.md, "Vector seeds"): the query is
/// embedded once, the vector query is scoped to what the caller may read before ranking, block hits collapse to their doc, and every
/// embedding failure degrades to lexical only with a reason code.
/// </summary>
public sealed class VectorSeedTests(ConstellationDb db) : IClassFixture<ConstellationDb>
{
    private const int _dims = 8;
    private const string _query = "how do we retry flaky deploys";

    private static readonly FakeEmbedder _provider = new(_dims);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    [Fact]
    public async Task Vector_seed_is_fused_with_lexical_seeds_and_the_result_is_not_lexical_only()
    {
        SkipWithoutDatabase();
        var (g, profile) = await SetupAsync();
        var lexicalDoc = await g.DocAsync($"agents/agent-a/memory/{g.Tag}-lex.md", "memory", $"Lex {g.Tag}", owner: "agent-a");
        var vectorDoc = await g.DocAsync($"agents/agent-a/memory/{g.Tag}-vec.md", "memory", $"Vec {g.Tag}", owner: "agent-a");
        await EmbedAsync(vectorDoc, 0, profile, _query);

        var retriever = Retriever(Selector(profile));
        var result = await RetrieveAsync(retriever, VaultPrincipal.Agent("agent-a"), [lexicalDoc]);

        Assert.False(result.LexicalOnly);
        Assert.Null(result.LexicalOnlyReason);
        Assert.Equal(new[] { lexicalDoc, vectorDoc }.Select(Id).Order(StringComparer.Ordinal), result.Hits.Select(h => h.DocId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Another_agents_private_blocks_never_seed_even_when_they_are_the_nearest()
    {
        SkipWithoutDatabase();
        var (g, profile) = await SetupAsync();
        var mine = await g.DocAsync($"agents/agent-a/memory/{g.Tag}-mine.md", "memory", $"Mine {g.Tag}", owner: "agent-a");
        var theirs = await g.DocAsync($"agents/agent-b/memory/{g.Tag}-theirs.md", "memory", $"Theirs {g.Tag}", owner: "agent-b");
        var profileDoc = await g.DocAsync($"agents/agent-b/profile-{g.Tag}.md", "charter", $"Profile {g.Tag}", owner: "agent-b");
        await EmbedAsync(theirs, 0, profile, _query);
        await EmbedAsync(profileDoc, 0, profile, _query);
        await EmbedAsync(mine, 0, profile, "something else entirely");

        // In SQL: with a limit of one block, agent B's exact match cannot take the place, because it is filtered before the limit.
        var nearest = await new PgMemoryVectorQuery(db.DataSource).NearestDocsAsync(
            profile.ProfileId,
            await _provider.EmbedAsync(_query, Ct),
            MemoryPathFilter.For(VaultPrincipal.Agent("agent-a"), null, g.VaultId),
            limit: 1,
            Ct);
        Assert.Equal([mine], nearest.Select(n => n.DocId));

        var result = await RetrieveAsync(Retriever(Selector(profile)), VaultPrincipal.Agent("agent-a"), []);
        Assert.Equal([Id(mine)], result.Hits.Select(h => h.DocId));

        // The owner reads all, so the same query seeds agent B's note for the owner.
        var owner = await RetrieveAsync(Retriever(Selector(profile)), VaultPrincipal.Owner(), []);
        Assert.Contains(owner.Hits, h => h.DocId == Id(theirs));
    }

    [Fact]
    public async Task Block_hits_collapse_to_their_doc_with_the_best_blocks_score()
    {
        SkipWithoutDatabase();
        var (g, profile) = await SetupAsync();
        var doc = await g.DocAsync($"agents/agent-a/memory/{g.Tag}-two.md", "memory", $"Two {g.Tag}", owner: "agent-a");
        await g.ExecAsync(
            "INSERT INTO vault_blocks (doc_id, ordinal, start_line, end_line, text_hash, body) VALUES ($1, 1, 2, 2, $2, 'second')",
            doc, new byte[32]);
        await EmbedAsync(doc, 0, profile, "unrelated words");
        await EmbedAsync(doc, 1, profile, _query);

        var nearest = await new PgMemoryVectorQuery(db.DataSource).NearestDocsAsync(
            profile.ProfileId,
            await _provider.EmbedAsync(_query, Ct),
            MemoryPathFilter.For(VaultPrincipal.Agent("agent-a"), null, g.VaultId),
            limit: 10,
            Ct);

        var single = Assert.Single(nearest);
        Assert.Equal(doc, single.DocId);
        Assert.Equal(1.0, single.Score, 2);
    }

    [Fact]
    public async Task Stale_vectors_and_other_profiles_do_not_seed()
    {
        SkipWithoutDatabase();
        var (g, profile) = await SetupAsync();
        var other = await CreateProfileAsync();
        var stale = await g.DocAsync($"agents/agent-a/memory/{g.Tag}-stale.md", "memory", $"Stale {g.Tag}", owner: "agent-a");
        var otherProfileDoc = await g.DocAsync($"agents/agent-a/memory/{g.Tag}-other.md", "memory", $"Other {g.Tag}", owner: "agent-a");
        await EmbedAsync(stale, 0, profile, _query, textHash: Enumerable.Repeat((byte)7, 32).ToArray());
        await EmbedAsync(otherProfileDoc, 0, other, _query);

        var result = await RetrieveAsync(Retriever(Selector(profile)), VaultPrincipal.Agent("agent-a"), []);

        Assert.Empty(result.Hits);
        Assert.True(result.LexicalOnly);
        Assert.Equal(MemoryLexicalOnlyReasons.NoVectorsYet, result.LexicalOnlyReason);
    }

    [Fact]
    public async Task Scope_prefixes_filter_vector_candidates_before_ranking()
    {
        SkipWithoutDatabase();
        var (g, profile) = await SetupAsync();
        var project = "p" + g.Tag;
        var inScope = await g.DocAsync($"agents/agent-a/memory/{g.Tag}-in.md", "memory", $"In {g.Tag}", owner: "agent-a");
        var outOfScope = await g.DocAsync($"projects/{project}/research/out.md", "research", $"Out {g.Tag}");
        await EmbedAsync(outOfScope, 0, profile, _query);
        await EmbedAsync(inScope, 0, profile, "a different text");

        var access = new VaultPathAccess(new StaticProjectMembership(new Dictionary<string, ProjectMembers>
        {
            [project] = new(new HashSet<string> { "agent-a" }, new HashSet<string>()),
        }));
        var source = new EmbeddingMemoryVectorSeedSource(Selector(profile), new PgMemoryVectorQuery(db.DataSource), access);

        var scoped = await source.GetSeedsAsync(VaultPrincipal.Agent("agent-a"), _query, ["agents/agent-a/memory/"], 1, Ct);
        var unscoped = await source.GetSeedsAsync(VaultPrincipal.Agent("agent-a"), _query, null, 1, Ct);

        Assert.Equal([Id(inScope)], scoped.Seeds.Select(s => s.Id));
        Assert.Equal([Id(outOfScope)], unscoped.Seeds.Select(s => s.Id));
    }

    [Fact]
    public async Task No_profile_is_lexical_only_with_profile_none_and_keeps_lexical_hits()
    {
        SkipWithoutDatabase();
        var (g, _) = await SetupAsync();
        var lexicalDoc = await g.DocAsync($"agents/agent-a/memory/{g.Tag}-lex.md", "memory", $"Lex {g.Tag}", owner: "agent-a");

        var withNoProfile = await RetrieveAsync(Retriever(null), VaultPrincipal.Agent("agent-a"), [lexicalDoc]);
        var withNoSource = await RetrieveAsync(
            new MemoryGraphRetriever(new PgConstellationNodeReader(db.DataSource, g.VaultId), Access()),
            VaultPrincipal.Agent("agent-a"), [lexicalDoc]);

        foreach (var result in new[] { withNoProfile, withNoSource })
        {
            Assert.True(result.LexicalOnly);
            Assert.Equal(MemoryLexicalOnlyReasons.ProfileNone, result.LexicalOnlyReason);
            Assert.Equal([Id(lexicalDoc)], result.Hits.Select(h => h.DocId));
        }
    }

    [Fact]
    public async Task Provider_failure_timeout_and_mismatch_degrade_to_lexical_only_with_a_reason_and_never_throw()
    {
        SkipWithoutDatabase();
        var (g, profile) = await SetupAsync();
        var lexicalDoc = await g.DocAsync($"agents/agent-a/memory/{g.Tag}-lex.md", "memory", $"Lex {g.Tag}", owner: "agent-a");
        var vectorDoc = await g.DocAsync($"agents/agent-a/memory/{g.Tag}-vec.md", "memory", $"Vec {g.Tag}", owner: "agent-a");
        await EmbedAsync(vectorDoc, 0, profile, _query);

        var cases = new (IVaultQueryEmbedder Embedder, string Reason)[]
        {
            (new ThrowingEmbedder(profile.ProfileId), MemoryLexicalOnlyReasons.ProviderError),
            (new SlowEmbedder(profile.ProfileId), MemoryLexicalOnlyReasons.ProviderTimeout),
            // An embedder that returns no vector does not match the profile.
            (new WrongLengthEmbedder(profile.ProfileId), MemoryLexicalOnlyReasons.ProfileMismatch),
            // A vector of another dimension than the stored profile fails the vector query.
            (new FakeEmbedder(4, profile.ProfileId), MemoryLexicalOnlyReasons.VectorQueryError),
        };

        foreach (var (embedder, reason) in cases)
        {
            var result = await RetrieveAsync(Retriever(embedder, TimeSpan.FromMilliseconds(100)), VaultPrincipal.Agent("agent-a"), [lexicalDoc]);
            Assert.True(result.LexicalOnly, reason);
            Assert.Equal(reason, result.LexicalOnlyReason);
            Assert.Equal([Id(lexicalDoc)], result.Hits.Select(h => h.DocId));
        }
    }

    [Fact]
    public async Task Provider_is_called_once_per_retrieval()
    {
        SkipWithoutDatabase();
        var (g, profile) = await SetupAsync();
        var counting = new CountingEmbedder(profile.ProfileId);
        await RetrieveAsync(Retriever(counting), VaultPrincipal.Agent("agent-a"), []);
        Assert.Equal(1, counting.Calls);
    }

    private async Task<(GraphSeed Seed, TestProfile Profile)> SetupAsync() => (new GraphSeed(db.DataSource), await CreateProfileAsync());

    private async Task<TestProfile> CreateProfileAsync()
    {
        var id = Guid.CreateVersion7();
        await new GraphSeed(db.DataSource).ExecAsync(
            "INSERT INTO embedding_profile (profile_id, provider_id, model_id, dimensions) VALUES ($1, 'fake', $2, $3)",
            id, "fake-model", _dims);
        return new TestProfile(id);
    }

    private async Task EmbedAsync(Guid docId, int ordinal, TestProfile profile, string text, byte[]? textHash = null)
    {
        var vector = await _provider.EmbedAsync(text, Ct);
        await new GraphSeed(db.DataSource).ExecAsync(
            "INSERT INTO vault_block_embedding (doc_id, ordinal, profile_id, text_hash, embedding) VALUES ($1, $2, $3, $4, $5::text::halfvec)",
            docId, ordinal, profile.ProfileId, textHash ?? new byte[32], PgMemoryVectorQuery.ToVectorLiteral(vector));
    }

    private MemoryGraphRetriever Retriever(IVaultQueryEmbedder? embedder, TimeSpan? timeout = null) =>
        new(
            new PgConstellationNodeReader(db.DataSource),
            Access(),
            new EmbeddingMemoryVectorSeedSource(embedder, new PgMemoryVectorQuery(db.DataSource), Access(), vaultId: null, timeout));

    private static VaultPathAccess Access() => new(new StaticProjectMembership(new Dictionary<string, ProjectMembers>()));

    private static FakeEmbedder Selector(TestProfile profile) => new FakeEmbedder(_dims, profile.ProfileId);

    private static Task<MemoryGraphRetrieval> RetrieveAsync(MemoryGraphRetriever retriever, VaultPrincipal who, Guid[] lexical) =>
        retriever.RetrieveForQueryAsync(
            who,
            _query,
            lexical.Select((id, i) => new RankedHit(Id(id), lexical.Length - i)).ToArray(),
            (ids, _) => Task.FromResult<IReadOnlyDictionary<string, int>>(ids.ToDictionary(id => id, _ => 1, StringComparer.Ordinal)),
            tokenBudget: 1000,
            new MemoryGraphOptions(MaxHops: 0),
            vectorCandidates: 50,
            Ct);

    private static string Id(Guid docId) => docId.ToString("D");

    private sealed record TestProfile(Guid ProfileId);

    /// <summary>Deterministic embedder: the same text gives the same unit vector; the profile is a parameter.</summary>
    private class FakeEmbedder(int dims, Guid? profileId = null) : IVaultQueryEmbedder
    {
        public Guid ProfileId { get; } = profileId ?? Guid.Empty;

        public virtual Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
            var vector = new float[dims];
            double norm = 0;
            for (var i = 0; i < dims; i++)
            {
                vector[i] = hash[i] - 127.5f;
                norm += vector[i] * vector[i];
            }

            var scale = (float)(1 / Math.Sqrt(norm));
            for (var i = 0; i < dims; i++)
            {
                vector[i] *= scale;
            }

            return Task.FromResult(vector);
        }
    }

    private sealed class ThrowingEmbedder(Guid profileId) : FakeEmbedder(_dims, profileId)
    {
        public override Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken) =>
            Task.FromException<float[]>(new InvalidOperationException("upstream failed"));
    }

    private sealed class SlowEmbedder(Guid profileId) : FakeEmbedder(_dims, profileId)
    {
        public override async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new float[_dims];
        }
    }

    private sealed class WrongLengthEmbedder(Guid profileId) : FakeEmbedder(_dims, profileId)
    {
        public override Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(Array.Empty<float>());
    }

    private sealed class CountingEmbedder(Guid profileId) : FakeEmbedder(_dims, profileId)
    {
        public int Calls { get; private set; }

        public override Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            Calls++;
            return base.EmbedAsync(text, cancellationToken);
        }
    }
}
