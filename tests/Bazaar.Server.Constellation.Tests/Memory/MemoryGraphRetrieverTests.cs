using Bazaar.Server.Constellation.Memory;
using Bazaar.Server.Memory.Access;
using Bazaar.Server.Memory.Retrieval;

namespace Bazaar.Server.Tests.Constellation.Memory;

public class MemoryGraphRetrieverTests
{
    private static readonly VaultPrincipal _owner = VaultPrincipal.Owner();
    private static readonly VaultPrincipal _agentA = VaultPrincipal.Agent("agent-a");
    private static readonly VaultPrincipal _agentB = VaultPrincipal.Agent("agent-b");
    private static readonly VaultPrincipal _member = VaultPrincipal.MemberHuman("member-1");

    private static readonly VaultPathAccess _access = new(
        new StaticProjectMembership(new Dictionary<string, ProjectMembers>()));

    private const int _defaultBudget = 1000;

    // The fused score of a seed at rank 1 in one list with the default RRF constant (60).
    private static double Rank1 => 1.0 / 61;

    [Fact]
    public async Task Seed_is_returned_at_hop_zero_with_its_fused_score()
    {
        var store = new FakeNodes(Note("a", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a"], vector: [], store: store);

        MemoryGraphHit hit = Assert.Single(result.Hits);
        Assert.Equal(Key("a"), hit.DocId);
        Assert.Equal("a", hit.NoteId);
        Assert.Equal(0, hit.HopDistance);
        Assert.Equal(Rank1, hit.Score, 12);
        Assert.Equal("agents/agent-a/memory/a.md", hit.VaultPath);
        Assert.Equal("agent-a", hit.ScopeAgentId);
    }

    [Fact]
    public async Task Link_neighbour_in_the_same_scope_is_expanded_at_hop_one()
    {
        var store = new FakeNodes(
            Note("a", "agent-a", Link("b")),
            Note("b", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a"], vector: [], store: store, maxHops: 1);

        Assert.Equal(["a", "b"], Names(result));
        MemoryGraphHit neighbour = result.Hits[1];
        Assert.Equal(1, neighbour.HopDistance);
        // Parent score * link weight 1.0 * hop penalty 0.5.
        Assert.Equal(Rank1 * 1.0 * 0.5, neighbour.Score, 12);
    }

    [Fact]
    public async Task Max_hops_zero_returns_seeds_only()
    {
        var store = new FakeNodes(
            Note("a", "agent-a", Link("b")),
            Note("b", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a"], vector: [], store: store, maxHops: 0);

        Assert.Equal(["a"], Names(result));
    }

    [Fact]
    public async Task Max_hops_above_two_is_capped_at_two()
    {
        var store = new FakeNodes(
            Note("a", "agent-a", Link("b")),
            Note("b", "agent-a", Link("c")),
            Note("c", "agent-a", Link("d")),
            Note("d", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a"], vector: [], store: store, maxHops: 5);

        Assert.Equal(["a", "b", "c"], Names(result));
        Assert.Equal([0, 1, 2], result.Hits.Select(h => h.HopDistance));
    }

    [Fact]
    public async Task Negative_max_hops_is_treated_as_zero()
    {
        var store = new FakeNodes(
            Note("a", "agent-a", Link("b")),
            Note("b", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a"], vector: [], store: store, maxHops: -3);

        Assert.Equal(["a"], Names(result));
    }

    [Fact]
    public async Task Linked_note_in_another_agents_private_area_is_never_returned_to_agent_a()
    {
        var store = new FakeNodes(
            Note("a", "agent-a", Link("secret")),
            Note("secret", "agent-b"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a"], vector: [], store: store, maxHops: 2);

        Assert.Equal(["a"], Names(result));
    }

    [Fact]
    public async Task Hidden_seed_from_another_agent_is_not_returned()
    {
        var store = new FakeNodes(Note("secret", "agent-b"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["secret"], vector: [], store: store);

        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task Hidden_note_is_not_a_hop_target_so_its_own_neighbours_are_not_reached()
    {
        // a (agent-a) -> secret (agent-b) -> own (agent-a). The hidden middle note must not carry expansion to own.
        var store = new FakeNodes(
            Note("a", "agent-a", Link("secret")),
            Note("secret", "agent-b", Link("own")),
            Note("own", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a"], vector: [], store: store, maxHops: 2);

        Assert.Equal(["a"], Names(result));
    }

    [Fact]
    public async Task Owner_sees_the_linked_note_in_another_agents_area()
    {
        var store = new FakeNodes(
            Note("a", "agent-a", Link("secret")),
            Note("secret", "agent-b"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _owner, lexical: ["a"], vector: [], store: store, maxHops: 1);

        Assert.Equal(["a", "secret"], Names(result));
    }

    [Fact]
    public async Task Deprecated_seed_is_never_returned()
    {
        var store = new FakeNodes(Note("old", "agent-a", status: "Deprecated"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["old"], vector: [], store: store);

        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task Deprecated_neighbour_is_never_returned_or_expanded()
    {
        var store = new FakeNodes(
            Note("a", "agent-a", Link("old")),
            Note("old", "agent-a", status: "Deprecated", Link("b")),
            Note("b", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a"], vector: [], store: store, maxHops: 2);

        Assert.Equal(["a"], Names(result));
    }

    [Fact]
    public async Task Neighbour_reached_by_two_paths_keeps_the_higher_score()
    {
        // Seeds a (rank 1) and b (rank 2). Both link to c, but a reaches it through a weight 0.8 edge and b through a
        // weight 1.0 edge. The b path scores higher: (1/62) * 1.0 * 0.5 beats (1/61) * 0.8 * 0.5.
        var store = new FakeNodes(
            Note("a", "agent-a", Edge("c", MemoryEdgeKind.DerivedFrom, 0.8)),
            Note("b", "agent-a", Link("c")),
            Note("c", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a", "b"], vector: [], store: store, maxHops: 1);

        MemoryGraphHit c = result.Hits.Single(h => h.DocId == Key("c"));
        Assert.Equal(1, c.HopDistance);
        Assert.Equal((1.0 / 62) * 1.0 * 0.5, c.Score, 12);
    }

    [Fact]
    public async Task Budget_drops_hits_that_do_not_fit_and_keeps_later_smaller_ones()
    {
        var store = new FakeNodes(
            Note("a", "agent-a"),
            Note("b", "agent-a"),
            Note("c", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);
        var counts = new Dictionary<string, int> { [Key("a")] = 10, [Key("b")] = 5, [Key("c")] = 2 };

        var result = await retriever.RetrieveAsync(
            _agentA,
            Ranked("a", "b", "c"),
            [],
            counts,
            tokenBudget: 12,
            new MemoryGraphOptions(MaxHops: 0),
            CancellationToken.None);

        // a fits (10). b does not (15 > 12) and is skipped. c fits (12).
        Assert.Equal(["a", "c"], Names(result));
    }

    [Fact]
    public async Task Candidate_without_a_token_count_is_skipped_without_throwing()
    {
        var store = new FakeNodes(
            Note("a", "agent-a"),
            Note("b", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);
        var counts = new Dictionary<string, int> { [Key("a")] = 1 };

        var result = await retriever.RetrieveAsync(
            _agentA,
            Ranked("a", "b"),
            [],
            counts,
            _defaultBudget,
            new MemoryGraphOptions(MaxHops: 0),
            CancellationToken.None);

        Assert.Equal(["a"], Names(result));
    }

    [Fact]
    public async Task LexicalOnly_is_true_when_vector_is_empty()
    {
        var store = new FakeNodes(Note("a", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a"], vector: [], store: store);

        Assert.True(result.LexicalOnly);
    }

    [Fact]
    public async Task LexicalOnly_is_false_when_vector_seeds_are_present()
    {
        var store = new FakeNodes(Note("a", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a"], vector: ["a"], store: store);

        Assert.False(result.LexicalOnly);
    }

    [Fact]
    public async Task DerivedFrom_edges_expand_and_a_Supersedes_target_is_history_not_a_current_hit()
    {
        var store = new FakeNodes(
            Note("a", "agent-a", Edge("derived", MemoryEdgeKind.DerivedFrom, 0.8), Edge("replaced", MemoryEdgeKind.Supersedes, 0.5)),
            Note("derived", "agent-a"),
            SupersededBy(Note("replaced", "agent-a"), "a"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a"], vector: [], store: store, maxHops: 1);

        Assert.Equal(["a", "derived"], Names(result));
        Assert.Equal(Rank1 * 0.8 * 0.5, result.Hits[1].Score, 12);
        Assert.All(result.Hits, h => Assert.False(h.IsHistory));

        var withHistory = await RetrieveAsync(retriever, _agentA, lexical: ["a"], vector: [], store: store, maxHops: 1, includeHistory: true);

        Assert.Equal(["a", "derived", "replaced"], Names(withHistory));
        MemoryGraphHit old = withHistory.Hits[2];
        Assert.True(old.IsHistory);
        Assert.Equal(Key("a"), old.SupersededBy);
        Assert.Equal(Rank1 * 0.5 * 0.5, old.Score, 12);
    }

    [Fact]
    public async Task Supersedes_chain_of_three_returns_only_the_head_even_when_the_oldest_ranks_first()
    {
        // c supersedes b, b supersedes a. The lexical ranking puts the oldest note first.
        var store = new FakeNodes(
            SupersededBy(Note("a", "agent-a"), "b"),
            SupersededBy(Note("b", "agent-a", Edge("a", MemoryEdgeKind.Supersedes, 0.5)), "c"),
            Note("c", "agent-a", Edge("b", MemoryEdgeKind.Supersedes, 0.5)));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a", "b", "c"], vector: [], store: store, maxHops: 2);

        MemoryGraphHit head = Assert.Single(result.Hits);
        Assert.Equal(Key("c"), head.DocId);
        Assert.False(head.IsHistory);
        Assert.Null(head.SupersededBy);
        // The head takes the best score of its chain, so it ranks where the oldest note would have ranked.
        Assert.Equal(Rank1, head.Score, 12);
        Assert.Equal(Key("a"), head.ViaDocId);
        Assert.Equal(0, head.HopDistance);
    }

    [Fact]
    public async Task Supersedes_chain_history_is_opt_in_labelled_and_never_ranked_above_its_head()
    {
        var store = new FakeNodes(
            SupersededBy(Note("a", "agent-a"), "b"),
            SupersededBy(Note("b", "agent-a", Edge("a", MemoryEdgeKind.Supersedes, 0.5)), "c"),
            Note("c", "agent-a", Edge("b", MemoryEdgeKind.Supersedes, 0.5)));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a", "b", "c"], vector: [], store: store, maxHops: 2, includeHistory: true);

        Assert.Equal(Key("c"), result.Hits[0].DocId);
        Assert.False(result.Hits[0].IsHistory);
        Assert.Equal(["a", "b"], Names(result).Skip(1).Order(StringComparer.Ordinal));
        Assert.All(result.Hits.Skip(1), h =>
        {
            Assert.True(h.IsHistory);
            Assert.Equal(Key("c"), h.SupersededBy);
            Assert.True(h.Score <= result.Hits[0].Score);
        });
    }

    [Fact]
    public async Task Superseded_seed_reached_alone_brings_in_its_head_instead()
    {
        var store = new FakeNodes(
            SupersededBy(Note("a", "agent-a"), "b"),
            SupersededBy(Note("b", "agent-a", Edge("a", MemoryEdgeKind.Supersedes, 0.5)), "c"),
            Note("c", "agent-a", Edge("b", MemoryEdgeKind.Supersedes, 0.5)));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a"], vector: [], store: store, maxHops: 0);

        Assert.Equal(["c"], Names(result));
        Assert.Equal(Key("a"), result.Hits[0].ViaDocId);
    }

    [Fact]
    public async Task A_superseder_the_caller_cannot_read_is_not_a_head_and_never_appears()
    {
        // Agent B's private note claims to supersede agent A's note; agent A must neither see it nor lose its own note to it.
        var store = new FakeNodes(
            SupersededBy(Note("a", "agent-a"), "b"),
            Note("b", "agent-b", Edge("a", MemoryEdgeKind.Supersedes, 0.5)));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a"], vector: [], store: store, maxHops: 1, includeHistory: true);

        MemoryGraphHit hit = Assert.Single(result.Hits);
        Assert.Equal(Key("a"), hit.DocId);
        Assert.False(hit.IsHistory);
        Assert.Null(hit.SupersededBy);
    }

    [Fact]
    public async Task Scope_prefixes_narrow_seeds_and_hop_targets()
    {
        var store = new FakeNodes(
            Note("a", "agent-a", Link("b")),
            Doc("b", "projects/alpha/decisions/b.md", "decision"),
            Doc("c", "projects/beta/decisions/c.md", "decision"));
        var retriever = new MemoryGraphRetriever(store, Access(("alpha", "agent-a"), ("beta", "agent-a")));
        var counts = store.Ids().ToDictionary(id => id, _ => 1, StringComparer.Ordinal);

        var result = await retriever.RetrieveAsync(
            _agentA,
            Ranked("a", "c"),
            [],
            counts,
            _defaultBudget,
            new MemoryGraphOptions(MaxHops: 1, ScopePrefixes: ["agents/agent-a/memory/"]),
            CancellationToken.None);

        Assert.Equal(["a"], Names(result));
    }

    [Fact]
    public async Task Provenance_edges_do_not_expand()
    {
        var store = new FakeNodes(
            Note("a", "agent-a", Edge("g1", MemoryEdgeKind.FromGc, 0.0), Edge("m1", MemoryEdgeKind.FromMessage, 0.0), Edge("agent-a", MemoryEdgeKind.AuthoredBy, 0.0)),
            Note("g1", "agent-a"),
            Note("m1", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a"], vector: [], store: store, maxHops: 2);

        Assert.Equal(["a"], Names(result));
    }

    [Fact]
    public async Task Empty_seeds_return_an_empty_result()
    {
        var store = new FakeNodes(Note("a", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: [], vector: [], store: store);

        Assert.Empty(result.Hits);
        Assert.True(result.LexicalOnly);
    }

    [Fact]
    public async Task Equal_scores_are_ordered_by_doc_id()
    {
        // b is rank 1 in the lexical list and a is rank 1 in the vector list, so both score 1/61.
        var store = new FakeNodes(
            Note("a", "agent-a"),
            Note("b", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["b"], vector: ["a"], store: store);

        Assert.Equal(["a", "b"], Names(result));
    }

    [Fact]
    public async Task Member_human_without_a_project_sees_nothing_in_an_agent_area()
    {
        var store = new FakeNodes(Note("a", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _member, lexical: ["a"], vector: [], store: store);

        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task Null_arguments_are_rejected()
    {
        var store = new FakeNodes(Note("a", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);
        var counts = new Dictionary<string, int>();
        var options = new MemoryGraphOptions();
        IReadOnlyList<RankedHit> list = [];

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            retriever.RetrieveAsync(null!, list, list, counts, 10, options, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            retriever.RetrieveAsync(_agentA, null!, list, counts, 10, options, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            retriever.RetrieveAsync(_agentA, list, null!, counts, 10, options, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            retriever.RetrieveAsync(_agentA, list, list, null!, 10, options, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            retriever.RetrieveAsync(_agentA, list, list, counts, 10, null!, CancellationToken.None));
    }

    [Fact]
    public void Constructor_rejects_null_dependencies()
    {
        Assert.Throws<ArgumentNullException>(() => new MemoryGraphRetriever(null!, _access));
        Assert.Throws<ArgumentNullException>(() => new MemoryGraphRetriever(new FakeNodes(), null!));
    }

    [Fact]
    public async Task Negative_token_budget_is_rejected()
    {
        var retriever = new MemoryGraphRetriever(new FakeNodes(), _access);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            retriever.RetrieveAsync(_agentA, [], [], new Dictionary<string, int>(), -1, new MemoryGraphOptions(), CancellationToken.None));
    }

    [Fact]
    public async Task Zero_top_k_is_rejected()
    {
        var retriever = new MemoryGraphRetriever(new FakeNodes(), _access);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            retriever.RetrieveAsync(_agentA, [], [], new Dictionary<string, int>(), 10, new MemoryGraphOptions(TopK: 0), CancellationToken.None));
    }

    [Fact]
    public async Task Zero_rrf_k_is_rejected()
    {
        var retriever = new MemoryGraphRetriever(new FakeNodes(), _access);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            retriever.RetrieveAsync(_agentA, [], [], new Dictionary<string, int>(), 10, new MemoryGraphOptions(RrfK: 0), CancellationToken.None));
    }

    [Fact]
    public async Task Memory_note_hops_to_a_project_decision_that_is_not_a_memory_note()
    {
        var access = Access(("home", "agent-a"));
        var store = new FakeNodes(
            Note("a", "agent-a", Link("decision")),
            Doc("decision", "projects/home/decisions/retry.md", "decision"));
        var retriever = new MemoryGraphRetriever(store, access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a"], vector: [], store: store, maxHops: 1);

        Assert.Equal(["a", "decision"], Names(result));
        MemoryGraphHit decision = result.Hits[1];
        Assert.Null(decision.NoteId);
        Assert.Null(decision.ScopeAgentId);
        Assert.Null(decision.Trust);
        Assert.Equal("decision", decision.Kind);
        Assert.Equal("projects/home/decisions/retry.md", decision.VaultPath);
        Assert.Equal(1, decision.HopDistance);
    }

    [Fact]
    public async Task Hop_into_a_linked_project_is_returned_only_when_the_callers_scope_allows_it()
    {
        // agent-a is a member of home and linked, not of foreign.
        var access = Access(("home", "agent-a"), ("linked", "agent-a"), ("foreign", "agent-b"));
        var store = new FakeNodes(
            Note("a", "agent-a", Link("home-doc"), Link("linked-doc"), Link("foreign-doc")),
            Doc("home-doc", "projects/home/research/r.md", "research"),
            Doc("linked-doc", "projects/linked/research/cache.md", "research"),
            Doc("foreign-doc", "projects/foreign/plans/p.md", "plan", Link("home-only")),
            Doc("home-only", "projects/home/plans/q.md", "plan"));
        var retriever = new MemoryGraphRetriever(store, access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a"], vector: [], store: store, maxHops: 2);

        Assert.Equal(["a", "home-doc", "linked-doc"], Names(result));
    }

    [Fact]
    public async Task Project_doc_linking_to_another_agents_private_note_never_returns_it()
    {
        var access = Access("home", "agent-a", "agent-b");
        var store = new FakeNodes(
            Doc("plan", "projects/home/plans/p.md", "plan", Link("secret")),
            Note("secret", "agent-b"));
        var retriever = new MemoryGraphRetriever(store, access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["plan"], vector: [], store: store, maxHops: 2);

        Assert.Equal(["plan"], Names(result));
    }

    [Fact]
    public async Task Memory_note_whose_revision_path_is_hidden_is_dropped_even_when_the_doc_path_is_visible()
    {
        // The index row says agent-a's area, but the active revision belongs to agent-b's private area: both must pass.
        var mismatched = Note("mixed", "agent-b") with { VaultPath = "agents/agent-a/memory/mixed.md" };
        var store = new FakeNodes(Note("a", "agent-a", Link("mixed")), mismatched);
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a", "mixed"], vector: [], store: store, maxHops: 1);

        Assert.Equal(["a"], Names(result));
    }

    [Fact]
    public async Task Doc_with_deprecated_front_matter_status_is_never_returned_or_expanded()
    {
        var access = Access(("home", "agent-a"));
        var store = new FakeNodes(
            Note("a", "agent-a", Link("old-plan")),
            Doc("old-plan", "projects/home/plans/old.md", "plan", Link("b")) with { FrontMatterStatus = "Deprecated" },
            Note("b", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a"], vector: [], store: store, maxHops: 2);

        Assert.Equal(["a"], Names(result));
    }

    [Fact]
    public async Task Seed_that_is_not_a_doc_id_is_dropped()
    {
        var store = new FakeNodes(Note("a", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);
        var counts = new Dictionary<string, int> { [Key("a")] = 1 };

        var result = await retriever.RetrieveAsync(
            _agentA,
            [new RankedHit("01NOTANID", 2), new RankedHit(Key("a"), 1)],
            [],
            counts,
            _defaultBudget,
            new MemoryGraphOptions(MaxHops: 0),
            CancellationToken.None);

        Assert.Equal(["a"], Names(result));
    }

    private static async Task<MemoryGraphRetrieval> RetrieveAsync(
        MemoryGraphRetriever retriever,
        VaultPrincipal who,
        string[] lexical,
        string[] vector,
        FakeNodes store,
        int maxHops = 1,
        bool includeHistory = false)
    {
        var counts = store.Ids().ToDictionary(id => id, _ => 1, StringComparer.Ordinal);
        return await retriever.RetrieveAsync(
            who,
            Ranked(lexical),
            Ranked(vector),
            counts,
            _defaultBudget,
            new MemoryGraphOptions(MaxHops: maxHops, IncludeHistory: includeHistory),
            CancellationToken.None);
    }

    /// <summary>Doc ids by test name. The first names get ids that sort in name order, so id tie-breaks read naturally.</summary>
    private static readonly string[] _ordered = ["a", "b", "c", "d", "home-doc", "linked-doc"];
    private static readonly Dictionary<string, Guid> _ids = new(StringComparer.Ordinal);
    private static readonly Dictionary<Guid, string> _names = [];

    private static Guid DocId(string name)
    {
        lock (_ids)
        {
            if (!_ids.TryGetValue(name, out Guid id))
            {
                int index = Array.IndexOf(_ordered, name);
                id = index >= 0 ? new Guid($"00000000-0000-0000-0000-{index + 1:D12}") : Guid.CreateVersion7();
                _ids[name] = id;
                _names[id] = name;
            }

            return id;
        }
    }

    private static string Key(string name) => DocId(name).ToString("D");

    private static string[] Names(MemoryGraphRetrieval result)
    {
        lock (_ids)
        {
            return [.. result.Hits.Select(h => _names[Guid.Parse(h.DocId)])];
        }
    }

    private static RankedHit[] Ranked(params string[] names) =>
        names.Select((name, i) => new RankedHit(Key(name), names.Length - i)).ToArray();

    private static VaultPathAccess Access(params (string Project, string Agent)[] members) =>
        AccessMany(members.Select(m => (m.Project, new[] { m.Agent })).ToArray());

    private static VaultPathAccess Access(string project, params string[] agents) => AccessMany([(project, agents)]);

    private static VaultPathAccess AccessMany((string Project, string[] Agents)[] members) =>
        new(new StaticProjectMembership(members
            .GroupBy(m => m.Project, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => new ProjectMembers(g.SelectMany(m => m.Agents).ToHashSet(StringComparer.Ordinal), new HashSet<string>()),
                StringComparer.Ordinal)));

    /// <summary>A memory note doc in its agent's private area.</summary>
    private static ConstellationGraphNode Note(string name, string scope, params NodeEdge[] edges) =>
        Note(name, scope, "Active", edges);

    private static ConstellationGraphNode Note(string name, string scope, string status, params NodeEdge[] edges)
    {
        string path = $"agents/{scope}/memory/{name}.md";
        var memory = new MemoryGraphRevision(
            NoteId: name,
            Version: 1,
            ScopeAgentId: scope,
            VaultPath: path,
            Title: name,
            Kind: "Semantic",
            Status: status,
            Trust: "Trusted",
            Edges: [],
            DocId: DocId(name));
        return new ConstellationGraphNode(DocId(name), path, name, "memory", null, memory, ToEdges(edges));
    }

    /// <summary>The node with incoming supersedes edges from <paramref name="by"/>, as the node reader reports them.</summary>
    private static ConstellationGraphNode SupersededBy(ConstellationGraphNode node, params string[] by) =>
        node with { SupersededBy = [.. by.Select(DocId).Order()] };

    /// <summary>A Vault doc that is not a memory note.</summary>
    private static ConstellationGraphNode Doc(string name, string path, string type, params NodeEdge[] edges) =>
        new(DocId(name), path, name, type, null, null, ToEdges(edges));

    private static ConstellationGraphEdge[] ToEdges(NodeEdge[] edges) =>
        [.. edges.Where(e => e.Kind is MemoryEdgeKind.Link or MemoryEdgeKind.DerivedFrom or MemoryEdgeKind.Supersedes)
            .Select(e => new ConstellationGraphEdge(DocId(e.Target), e.Kind, e.Weight))];

    private static NodeEdge Link(string target) => Edge(target, MemoryEdgeKind.Link, 1.0);

    private static NodeEdge Edge(string target, MemoryEdgeKind kind, double weight) => new(target, kind, weight);

    private sealed record NodeEdge(string Target, MemoryEdgeKind Kind, double Weight);

    /// <summary>In-memory node reader: one node per doc, with its active memory revision and edges.</summary>
    private sealed class FakeNodes : IConstellationNodeReader
    {
        private readonly Dictionary<Guid, ConstellationGraphNode> _nodes = [];

        public FakeNodes(params ConstellationGraphNode[] nodes)
        {
            foreach (ConstellationGraphNode node in nodes)
            {
                _nodes[node.DocId] = node;
            }
        }

        public string[] Ids() => [.. _nodes.Keys.Select(id => id.ToString("D"))];

        public Task<IReadOnlyList<ConstellationGraphNode>> GetNodesAsync(IReadOnlyCollection<Guid> docIds, CancellationToken ct)
        {
            IReadOnlyList<ConstellationGraphNode> found = [.. docIds.Where(_nodes.ContainsKey).Select(id => _nodes[id])];
            return Task.FromResult(found);
        }
    }
}
