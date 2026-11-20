using Bazaar.Server.Constellation.Memory;
using Bazaar.Server.Embeddings;
using Bazaar.Server.Memory.Access;
using Bazaar.Server.Memory.Retrieval;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Constellation.Memory;

/// <summary>
/// Vector seeds on real PostgreSQL with a fake provider (docs/design/constellation-memory-graph.md, "Vector seeds"): the query is
/// embedded once, the vector query is scoped to what the caller may read before ranking, block hits collapse to their doc, and every
/// embedding failure degrades to lexical only with a reason code.
/// </summary>
public sealed class VectorSeedTests(ConstellationDb db) : IClassFixture<ConstellationDb>
{
    private const int _dims = 8;
    private const string _query = "how do we retry flaky deploys";

    private static readonly FakeEmbeddingProvider _provider = new(_dims);

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
            (await _provider.EmbedAsync([_query], Ct)).Vectors[0],
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
            (await _provider.EmbedAsync([_query], Ct)).Vectors[0],
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

        var withNoProfile = await RetrieveAsync(Retriever(new FixedSelector(null)), VaultPrincipal.Agent("agent-a"), [lexicalDoc]);
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

        var cases = new (IActiveEmbeddingSelector Selector, string Reason)[]
        {
            (new FixedSelector(new ActiveEmbedding(profile, new ThrowingProvider(_dims))), MemoryLexicalOnlyReasons.ProviderError),
            (new FixedSelector(new ActiveEmbedding(profile, new SlowProvider(_dims))), MemoryLexicalOnlyReasons.ProviderTimeout),
            // A provider of another dimension does not match the profile: refused before any call.
            (new FixedSelector(new ActiveEmbedding(profile, new FakeEmbeddingProvider(4))), MemoryLexicalOnlyReasons.ProfileMismatch),
            // A provider that claims the profile's dimension but returns another length.
            (new FixedSelector(new ActiveEmbedding(profile, new WrongLengthProvider(_dims))), MemoryLexicalOnlyReasons.ProfileMismatch),
            (new ThrowingSelector(new EmbeddingProfileMismatchException("mismatch")), MemoryLexicalOnlyReasons.ProfileMismatch),
            (new ThrowingSelector(new EmbeddingException("down")), MemoryLexicalOnlyReasons.ProviderError),
        };

        foreach (var (selector, reason) in cases)
        {
            var result = await RetrieveAsync(Retriever(selector, TimeSpan.FromMilliseconds(100)), VaultPrincipal.Agent("agent-a"), [lexicalDoc]);
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
        var counting = new CountingProvider(_provider);
        await RetrieveAsync(Retriever(new FixedSelector(new ActiveEmbedding(profile, counting))), VaultPrincipal.Agent("agent-a"), []);
        Assert.Equal(1, counting.Calls);
    }

    private async Task<(GraphSeed Seed, EmbeddingProfile Profile)> SetupAsync() => (new GraphSeed(db.DataSource), await CreateProfileAsync());

    private async Task<EmbeddingProfile> CreateProfileAsync()
    {
        var id = Guid.CreateVersion7();
        await new GraphSeed(db.DataSource).ExecAsync(
            "INSERT INTO embedding_profile (profile_id, provider_id, model_id, dimensions) VALUES ($1, 'fake', $2, $3)",
            id, FakeEmbeddingProvider.DefaultModelId, _dims);
        return new EmbeddingProfile(id, "fake", FakeEmbeddingProvider.DefaultModelId, _dims, DateTimeOffset.UtcNow, Active: true);
    }

    private async Task EmbedAsync(Guid docId, int ordinal, EmbeddingProfile profile, string text, byte[]? textHash = null)
    {
        var vector = (await _provider.EmbedAsync([text], Ct)).Vectors[0];
        await new GraphSeed(db.DataSource).ExecAsync(
            "INSERT INTO vault_block_embedding (doc_id, ordinal, profile_id, text_hash, embedding) VALUES ($1, $2, $3, $4, $5::text::halfvec)",
            docId, ordinal, profile.ProfileId, textHash ?? new byte[32], PgMemoryVectorQuery.ToVectorLiteral(vector));
    }

    private MemoryGraphRetriever Retriever(IActiveEmbeddingSelector selector, TimeSpan? timeout = null) =>
        new(
            new PgConstellationNodeReader(db.DataSource),
            Access(),
            new EmbeddingMemoryVectorSeedSource(selector, new PgMemoryVectorQuery(db.DataSource), Access(), vaultId: null, timeout));

    private static VaultPathAccess Access() => new(new StaticProjectMembership(new Dictionary<string, ProjectMembers>()));

    private static FixedSelector Selector(EmbeddingProfile profile) => new FixedSelector(new ActiveEmbedding(profile, _provider));

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

    private sealed class FixedSelector(ActiveEmbedding? active) : IActiveEmbeddingSelector
    {
        public Task<ActiveEmbedding?> ResolveAsync(CancellationToken cancellationToken) => Task.FromResult(active);
    }

    private sealed class ThrowingSelector(Exception error) : IActiveEmbeddingSelector
    {
        public Task<ActiveEmbedding?> ResolveAsync(CancellationToken cancellationToken) => Task.FromException<ActiveEmbedding?>(error);
    }

    private sealed class ThrowingProvider(int dims) : IEmbeddingProvider
    {
        public EmbeddingProviderDescriptor Descriptor { get; } = new(EmbeddingProviderKind.Fake, FakeEmbeddingProvider.DefaultModelId, dims);

        public Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            Task.FromException<EmbeddingResult>(new EmbeddingException("upstream failed"));
    }

    private sealed class SlowProvider(int dims) : IEmbeddingProvider
    {
        public EmbeddingProviderDescriptor Descriptor { get; } = new(EmbeddingProviderKind.Fake, FakeEmbeddingProvider.DefaultModelId, dims);

        public async Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new EmbeddingResult([new float[dims]]);
        }
    }

    private sealed class WrongLengthProvider(int dims) : IEmbeddingProvider
    {
        public EmbeddingProviderDescriptor Descriptor { get; } = new(EmbeddingProviderKind.Fake, FakeEmbeddingProvider.DefaultModelId, dims);

        public Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            Task.FromResult(new EmbeddingResult([new float[dims + 1]]));
    }

    private sealed class CountingProvider(IEmbeddingProvider inner) : IEmbeddingProvider
    {
        public int Calls { get; private set; }

        public EmbeddingProviderDescriptor Descriptor => inner.Descriptor;

        public Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            Calls++;
            return inner.EmbedAsync(texts, cancellationToken);
        }
    }
}
