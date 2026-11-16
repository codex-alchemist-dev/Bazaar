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

    private const int DefaultBudget = 1000;

    // The fused score of a seed at rank 1 in one list with the default RRF constant (60).
    private static double Rank1 => 1.0 / 61;

    [Fact]
    public async Task Seed_is_returned_at_hop_zero_with_its_fused_score()
    {
        var store = new FakeStore(Note("a", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a"], vector: [], store: store);

        MemoryGraphHit hit = Assert.Single(result.Hits);
        Assert.Equal("a", hit.NoteId);
        Assert.Equal(0, hit.HopDistance);
        Assert.Equal(Rank1, hit.Score, 12);
        Assert.Equal("agents/agent-a/memory/a.md", hit.VaultPath);
        Assert.Equal("agent-a", hit.ScopeAgentId);
    }

    [Fact]
    public async Task Link_neighbour_in_the_same_scope_is_expanded_at_hop_one()
    {
        var store = new FakeStore(
            Note("a", "agent-a", Link("b")),
            Note("b", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a"], vector: [], store: store, maxHops: 1);

        Assert.Equal(["a", "b"], result.Hits.Select(h => h.NoteId));
        MemoryGraphHit neighbour = result.Hits[1];
        Assert.Equal(1, neighbour.HopDistance);
        // Parent score * link weight 1.0 * hop penalty 0.5.
        Assert.Equal(Rank1 * 1.0 * 0.5, neighbour.Score, 12);
    }

    [Fact]
    public async Task Max_hops_zero_returns_seeds_only()
    {
        var store = new FakeStore(
            Note("a", "agent-a", Link("b")),
            Note("b", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a"], vector: [], store: store, maxHops: 0);

        Assert.Equal(["a"], result.Hits.Select(h => h.NoteId));
    }

    [Fact]
    public async Task Max_hops_above_two_is_capped_at_two()
    {
        var store = new FakeStore(
            Note("a", "agent-a", Link("b")),
            Note("b", "agent-a", Link("c")),
            Note("c", "agent-a", Link("d")),
            Note("d", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a"], vector: [], store: store, maxHops: 5);

        Assert.Equal(["a", "b", "c"], result.Hits.Select(h => h.NoteId));
        Assert.Equal([0, 1, 2], result.Hits.Select(h => h.HopDistance));
    }

    [Fact]
    public async Task Negative_max_hops_is_treated_as_zero()
    {
        var store = new FakeStore(
            Note("a", "agent-a", Link("b")),
            Note("b", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a"], vector: [], store: store, maxHops: -3);

        Assert.Equal(["a"], result.Hits.Select(h => h.NoteId));
    }

    [Fact]
    public async Task Linked_note_in_another_agents_private_area_is_never_returned_to_agent_a()
    {
        var store = new FakeStore(
            Note("a", "agent-a", Link("secret")),
            Note("secret", "agent-b"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a"], vector: [], store: store, maxHops: 2);

        Assert.Equal(["a"], result.Hits.Select(h => h.NoteId));
    }

    [Fact]
    public async Task Hidden_seed_from_another_agent_is_not_returned()
    {
        var store = new FakeStore(Note("secret", "agent-b"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["secret"], vector: [], store: store);

        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task Hidden_note_is_not_a_hop_target_so_its_own_neighbours_are_not_reached()
    {
        // a (agent-a) -> secret (agent-b) -> own (agent-a). The hidden middle note must not carry expansion to own.
        var store = new FakeStore(
            Note("a", "agent-a", Link("secret")),
            Note("secret", "agent-b", Link("own")),
            Note("own", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a"], vector: [], store: store, maxHops: 2);

        Assert.Equal(["a"], result.Hits.Select(h => h.NoteId));
    }

    [Fact]
    public async Task Owner_sees_the_linked_note_in_another_agents_area()
    {
        var store = new FakeStore(
            Note("a", "agent-a", Link("secret")),
            Note("secret", "agent-b"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _owner, lexical: ["a"], vector: [], store: store, maxHops: 1);

        Assert.Equal(["a", "secret"], result.Hits.Select(h => h.NoteId));
    }

    [Fact]
    public async Task Deprecated_seed_is_never_returned()
    {
        var store = new FakeStore(Note("old", "agent-a", status: "Deprecated"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["old"], vector: [], store: store);

        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task Deprecated_neighbour_is_never_returned_or_expanded()
    {
        var store = new FakeStore(
            Note("a", "agent-a", Link("old")),
            Note("old", "agent-a", status: "Deprecated", Link("b")),
            Note("b", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a"], vector: [], store: store, maxHops: 2);

        Assert.Equal(["a"], result.Hits.Select(h => h.NoteId));
    }

    [Fact]
    public async Task Neighbour_reached_by_two_paths_keeps_the_higher_score()
    {
        // Seeds a (rank 1) and b (rank 2). Both link to c, but a reaches it through a weight 0.8 edge and b through a
        // weight 1.0 edge. The b path scores higher: (1/62) * 1.0 * 0.5 beats (1/61) * 0.8 * 0.5.
        var store = new FakeStore(
            Note("a", "agent-a", Edge("c", MemoryEdgeKind.DerivedFrom, 0.8)),
            Note("b", "agent-a", Link("c")),
            Note("c", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a", "b"], vector: [], store: store, maxHops: 1);

        MemoryGraphHit c = result.Hits.Single(h => h.NoteId == "c");
        Assert.Equal(1, c.HopDistance);
        Assert.Equal((1.0 / 62) * 1.0 * 0.5, c.Score, 12);
    }

    [Fact]
    public async Task Budget_drops_hits_that_do_not_fit_and_keeps_later_smaller_ones()
    {
        var store = new FakeStore(
            Note("a", "agent-a"),
            Note("b", "agent-a"),
            Note("c", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);
        var counts = new Dictionary<string, int> { ["a"] = 10, ["b"] = 5, ["c"] = 2 };

        var result = await retriever.RetrieveAsync(
            _agentA,
            Ranked("a", "b", "c"),
            [],
            counts,
            tokenBudget: 12,
            new MemoryGraphOptions(MaxHops: 0),
            CancellationToken.None);

        // a fits (10). b does not (15 > 12) and is skipped. c fits (12).
        Assert.Equal(["a", "c"], result.Hits.Select(h => h.NoteId));
    }

    [Fact]
    public async Task Candidate_without_a_token_count_is_skipped_without_throwing()
    {
        var store = new FakeStore(
            Note("a", "agent-a"),
            Note("b", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);
        var counts = new Dictionary<string, int> { ["a"] = 1 };

        var result = await retriever.RetrieveAsync(
            _agentA,
            Ranked("a", "b"),
            [],
            counts,
            DefaultBudget,
            new MemoryGraphOptions(MaxHops: 0),
            CancellationToken.None);

        Assert.Equal(["a"], result.Hits.Select(h => h.NoteId));
    }

    [Fact]
    public async Task LexicalOnly_is_true_when_vector_is_empty()
    {
        var store = new FakeStore(Note("a", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a"], vector: [], store: store);

        Assert.True(result.LexicalOnly);
    }

    [Fact]
    public async Task LexicalOnly_is_false_when_vector_seeds_are_present()
    {
        var store = new FakeStore(Note("a", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a"], vector: ["a"], store: store);

        Assert.False(result.LexicalOnly);
    }

    [Fact]
    public async Task DerivedFrom_and_Supersedes_edges_expand()
    {
        var store = new FakeStore(
            Note("a", "agent-a", Edge("derived", MemoryEdgeKind.DerivedFrom, 0.8), Edge("replaced", MemoryEdgeKind.Supersedes, 0.5)),
            Note("derived", "agent-a"),
            Note("replaced", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a"], vector: [], store: store, maxHops: 1);

        Assert.Equal(["a", "derived", "replaced"], result.Hits.Select(h => h.NoteId));
        Assert.Equal(Rank1 * 0.8 * 0.5, result.Hits[1].Score, 12);
        Assert.Equal(Rank1 * 0.5 * 0.5, result.Hits[2].Score, 12);
    }

    [Fact]
    public async Task Provenance_edges_do_not_expand()
    {
        var store = new FakeStore(
            Note("a", "agent-a", Edge("g1", MemoryEdgeKind.FromGc, 0.0), Edge("m1", MemoryEdgeKind.FromMessage, 0.0), Edge("agent-a", MemoryEdgeKind.AuthoredBy, 0.0)),
            Note("g1", "agent-a"),
            Note("m1", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["a"], vector: [], store: store, maxHops: 2);

        Assert.Equal(["a"], result.Hits.Select(h => h.NoteId));
    }

    [Fact]
    public async Task Empty_seeds_return_an_empty_result()
    {
        var store = new FakeStore(Note("a", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: [], vector: [], store: store);

        Assert.Empty(result.Hits);
        Assert.True(result.LexicalOnly);
    }

    [Fact]
    public async Task Equal_scores_are_ordered_by_note_id()
    {
        // b is rank 1 in the lexical list and a is rank 1 in the vector list, so both score 1/61.
        var store = new FakeStore(
            Note("a", "agent-a"),
            Note("b", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _agentA, lexical: ["b"], vector: ["a"], store: store);

        Assert.Equal(["a", "b"], result.Hits.Select(h => h.NoteId));
    }

    [Fact]
    public async Task Member_human_without_a_project_sees_nothing_in_an_agent_area()
    {
        var store = new FakeStore(Note("a", "agent-a"));
        var retriever = new MemoryGraphRetriever(store, _access);

        var result = await RetrieveAsync(retriever, _member, lexical: ["a"], vector: [], store: store);

        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task Null_arguments_are_rejected()
    {
        var store = new FakeStore(Note("a", "agent-a"));
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
        Assert.Throws<ArgumentNullException>(() => new MemoryGraphRetriever(new FakeStore(), null!));
    }

    [Fact]
    public async Task Negative_token_budget_is_rejected()
    {
        var retriever = new MemoryGraphRetriever(new FakeStore(), _access);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            retriever.RetrieveAsync(_agentA, [], [], new Dictionary<string, int>(), -1, new MemoryGraphOptions(), CancellationToken.None));
    }

    [Fact]
    public async Task Zero_top_k_is_rejected()
    {
        var retriever = new MemoryGraphRetriever(new FakeStore(), _access);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            retriever.RetrieveAsync(_agentA, [], [], new Dictionary<string, int>(), 10, new MemoryGraphOptions(TopK: 0), CancellationToken.None));
    }

    [Fact]
    public async Task Zero_rrf_k_is_rejected()
    {
        var retriever = new MemoryGraphRetriever(new FakeStore(), _access);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            retriever.RetrieveAsync(_agentA, [], [], new Dictionary<string, int>(), 10, new MemoryGraphOptions(RrfK: 0), CancellationToken.None));
    }

    private static async Task<MemoryGraphRetrieval> RetrieveAsync(
        MemoryGraphRetriever retriever,
        VaultPrincipal who,
        string[] lexical,
        string[] vector,
        FakeStore store,
        int maxHops = 1)
    {
        var counts = store.Ids().ToDictionary(id => id, _ => 1, StringComparer.Ordinal);
        return await retriever.RetrieveAsync(
            who,
            Ranked(lexical),
            Ranked(vector),
            counts,
            DefaultBudget,
            new MemoryGraphOptions(MaxHops: maxHops),
            CancellationToken.None);
    }

    private static RankedHit[] Ranked(params string[] ids) =>
        ids.Select((id, i) => new RankedHit(id, ids.Length - i)).ToArray();

    private static MemoryGraphRevision Note(
        string id,
        string scope,
        params MemoryGraphEdge[] edges) => Note(id, scope, "Active", edges);

    private static MemoryGraphRevision Note(
        string id,
        string scope,
        string status,
        params MemoryGraphEdge[] edges) =>
        new(
            NoteId: id,
            Version: 1,
            ScopeAgentId: scope,
            VaultPath: $"agents/{scope}/memory/{id}.md",
            Title: id,
            Kind: "Semantic",
            Status: status,
            Trust: "Trusted",
            Edges: edges);

    private static MemoryGraphEdge Link(string target) => Edge(target, MemoryEdgeKind.Link, 1.0);

    private static MemoryGraphEdge Edge(string target, MemoryEdgeKind kind, double weight) =>
        new(target, kind, weight);

    /// <summary>In-memory store that holds one active revision per note. Revision history is not needed here.</summary>
    private sealed class FakeStore : IMemoryGraphStore
    {
        private readonly Dictionary<string, MemoryGraphRevision> _active = new(StringComparer.Ordinal);

        public FakeStore(params MemoryGraphRevision[] revisions)
        {
            foreach (MemoryGraphRevision revision in revisions)
            {
                _active[revision.NoteId] = revision;
            }
        }

        public string[] Ids() => _active.Keys.ToArray();

        public Task StoreRevisionAsync(MemoryGraphRevision revision, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> ActivateRevisionAsync(string noteId, long version, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MemoryGraphRevision>> GetActiveAsync(IReadOnlyCollection<string> noteIds, CancellationToken ct)
        {
            IReadOnlyList<MemoryGraphRevision> found = noteIds
                .Where(_active.ContainsKey)
                .Select(id => _active[id])
                .ToArray();
            return Task.FromResult(found);
        }

        public Task<IReadOnlyList<MemoryGraphRevision>> ListRevisionsAsync(string noteId, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
