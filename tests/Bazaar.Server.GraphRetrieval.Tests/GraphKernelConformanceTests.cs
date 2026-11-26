namespace Bazaar.Server.GraphRetrieval.Tests;

/// <summary>
/// Behaviour every <see cref="IGraphKernel"/> must show, whatever the engine. A GPU provider derives from this class
/// and returns its own kernel from <see cref="CreateKernel"/>; it must pass unchanged (doc 64 sections 8 and 14).
/// </summary>
public abstract class GraphKernelConformanceTests
{
    private const double _eps = 1e-9;

    /// <summary>Creates the kernel under test. <paramref name="threads"/> is a hint; engines without threads ignore it.</summary>
    protected abstract IGraphKernel CreateKernel(int threads);

    private static CancellationToken _ct => TestContext.Current.CancellationToken;

    private static async Task<T> Ok<T>(ValueTask<KernelResult<T>> call)
        where T : class
    {
        KernelResult<T> r = await call;
        Assert.Equal(DeclineReason.None, r.Reason);
        Assert.True(r.Succeeded);
        return r.Value!;
    }

    private Task<ExpansionResult> Expand(TestGraph g, ExpansionRequest r, int threads = 1) => Ok(CreateKernel(threads).TryExpandAsync(r, _ct));

    private static ExpansionRequest ExpandReq(TestGraph g, IEnumerable<SeedScore> seeds, int hops = 2) =>
        new() { Snapshot = g.Snapshot, Seeds = [.. seeds], MaxHops = hops };

    private static double ScoreOf(ExpansionResult r, int node) => r.TryGet(node, out ReachedNode n) ? n.Score : double.NaN;

    // ---------------------------------------------------------------- expansion

    [Fact]
    public async Task Capabilities_are_declared()
    {
        KernelCapabilities c = CreateKernel(1).Capabilities;
        Assert.False(string.IsNullOrEmpty(c.Name));
        Assert.True(c.Expand && c.Closure && c.Rank && c.Subgraph && c.Deterministic);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Expansion_over_empty_graph_returns_nothing()
    {
        var g = new TestGraph();
        ExpansionResult r = await Expand(g, ExpandReq(g, []));
        Assert.Empty(r.Nodes);
    }

    [Fact]
    public async Task Expansion_echoes_the_snapshot_watermark()
    {
        var g = new TestGraph().Edge("A", "B");
        KernelResult<ExpansionResult> r = await CreateKernel(1).TryExpandAsync(ExpandReq(g, [g.Seed("A")]), _ct);
        Assert.Equal(42, r.IndexWatermark);
    }

    [Fact]
    public async Task Expansion_declines_a_seed_outside_the_graph()
    {
        var g = new TestGraph().Edge("A", "B");
        KernelResult<ExpansionResult> r = await CreateKernel(1).TryExpandAsync(ExpandReq(g, [new SeedScore(99, 1)]), _ct);
        Assert.Equal(DeclineReason.InvalidRequest, r.Reason);
        Assert.Null(r.Value);
    }

    [Fact]
    public async Task Expansion_declines_bad_scores_and_decay_and_mask_length()
    {
        var g = new TestGraph().Edge("A", "B");
        IGraphKernel k = CreateKernel(1);
        Assert.Equal(DeclineReason.InvalidRequest, (await k.TryExpandAsync(ExpandReq(g, [new SeedScore(0, double.NaN)]), _ct)).Reason);
        Assert.Equal(DeclineReason.InvalidRequest, (await k.TryExpandAsync(ExpandReq(g, [new SeedScore(0, -1)]), _ct)).Reason);
        Assert.Equal(DeclineReason.InvalidRequest, (await k.TryExpandAsync(ExpandReq(g, [g.Seed("A")]) with { HopDecay = 0 }, _ct)).Reason);
        Assert.Equal(DeclineReason.InvalidRequest, (await k.TryExpandAsync(ExpandReq(g, [g.Seed("A")]) with { HopDecay = 1.5 }, _ct)).Reason);
        Assert.Equal(DeclineReason.InvalidRequest, (await k.TryExpandAsync(ExpandReq(g, [g.Seed("A")], -1), _ct)).Reason);
        Assert.Equal(DeclineReason.InvalidRequest, (await k.TryExpandAsync(ExpandReq(g, [g.Seed("A")]) with { Mask = NodeMask.All(5) }, _ct)).Reason);
    }

    [Fact]
    public async Task Expansion_applies_edge_weight_and_decay_per_hop()
    {
        var g = new TestGraph().Edge("A", "B").Edge("B", "C");
        ExpansionResult r = await Expand(g, ExpandReq(g, [g.Seed("A")]));
        Assert.Equal(1.0, ScoreOf(r, g.N("A")), _eps);
        Assert.Equal(0.5, ScoreOf(r, g.N("B")), _eps);
        Assert.Equal(0.25, ScoreOf(r, g.N("C")), _eps);
        Assert.Equal(2, r.Nodes.Single(n => n.Node == g.N("C")).Level);
        Assert.Equal(0, r.Nodes.Single(n => n.Node == g.N("A")).Level);
    }

    [Fact]
    public async Task Expansion_uses_the_default_kind_weights()
    {
        var g = new TestGraph()
            .Edge("A", "D", GraphEdgeKind.DerivedFrom)
            .Edge("A", "S", GraphEdgeKind.Supersedes)
            .Edge("A", "K", GraphEdgeKind.ApprovedBy);
        ExpansionResult r = await Expand(g, ExpandReq(g, [g.Seed("A")], 1));
        // Weights are stored as float, so compare at float precision.
        Assert.Equal(0.4, ScoreOf(r, g.N("D")), 1e-6);
        Assert.Equal(0.25, ScoreOf(r, g.N("S")), 1e-6);
        Assert.Equal(0.15, ScoreOf(r, g.N("K")), 1e-6);
    }

    [Fact]
    public async Task Expansion_honours_a_custom_decay()
    {
        var g = new TestGraph().Edge("A", "B").Edge("B", "C");
        ExpansionResult r = await Expand(g, ExpandReq(g, [g.Seed("A", 2)]) with { HopDecay = 0.8 });
        Assert.Equal(1.6, ScoreOf(r, g.N("B")), _eps);
        Assert.Equal(1.28, ScoreOf(r, g.N("C")), _eps);
    }

    [Fact]
    public async Task Hop_budget_limits_depth()
    {
        var g = new TestGraph().Edge("A", "B").Edge("B", "C").Edge("C", "D");
        ExpansionResult none = await Expand(g, ExpandReq(g, [g.Seed("A")], 0));
        Assert.Equal([g.N("A")], none.Nodes.Select(n => n.Node));
        ExpansionResult one = await Expand(g, ExpandReq(g, [g.Seed("A")], 1));
        Assert.Equal(2, one.Nodes.Count);
        ExpansionResult three = await Expand(g, ExpandReq(g, [g.Seed("A")], 3));
        Assert.Equal(4, three.Nodes.Count);
        ExpansionResult many = await Expand(g, ExpandReq(g, [g.Seed("A")], 50));
        Assert.Equal(4, many.Nodes.Count);
    }

    [Fact]
    public async Task Zero_weight_edges_are_never_followed()
    {
        var g = new TestGraph()
            .Edge("A", "B", GraphEdgeKind.Link, 0.0)
            .Edge("A", "C", GraphEdgeKind.Contradicts)
            .Edge("A", "D");
        ExpansionResult r = await Expand(g, ExpandReq(g, [g.Seed("A")]));
        Assert.Equal(new[] { g.N("A"), g.N("D") }.Order(), r.Nodes.Select(n => n.Node).Order());
    }

    [Fact]
    public async Task Kind_filter_restricts_the_walk()
    {
        var g = new TestGraph().Edge("A", "B", GraphEdgeKind.Link).Edge("A", "C", GraphEdgeKind.DerivedFrom);
        ExpansionResult r = await Expand(g, ExpandReq(g, [g.Seed("A")]) with { Kinds = EdgeKindSet.Of(GraphEdgeKind.DerivedFrom) });
        Assert.False(r.TryGet(g.N("B"), out _));
        Assert.True(r.TryGet(g.N("C"), out _));
    }

    [Fact]
    public async Task Direction_controls_which_edges_are_followed()
    {
        var g = new TestGraph().Edge("A", "B").Edge("C", "A");
        ExpansionResult outgoing = await Expand(g, ExpandReq(g, [g.Seed("A")], 1));
        Assert.True(outgoing.TryGet(g.N("B"), out _));
        Assert.False(outgoing.TryGet(g.N("C"), out _));

        ExpansionResult incoming = await Expand(g, ExpandReq(g, [g.Seed("A")], 1) with { Direction = EdgeDirection.Incoming });
        Assert.False(incoming.TryGet(g.N("B"), out _));
        Assert.Equal(0.5, ScoreOf(incoming, g.N("C")), _eps);

        ExpansionResult both = await Expand(g, ExpandReq(g, [g.Seed("A")], 1) with { Direction = EdgeDirection.Both });
        Assert.Equal(0.5, ScoreOf(both, g.N("B")), _eps);
        Assert.Equal(0.25, ScoreOf(both, g.N("C")), _eps); // backlink at half weight
        ExpansionResult custom = await Expand(g, ExpandReq(g, [g.Seed("A")], 1) with { Direction = EdgeDirection.Both, ReverseWeightFactor = 1.0 });
        Assert.Equal(0.5, ScoreOf(custom, g.N("C")), _eps);
    }

    [Fact]
    public async Task Best_path_wins_and_the_traced_path_explains_the_score()
    {
        var g = new TestGraph()
            .Edge("A", "C", GraphEdgeKind.Link, 0.2)
            .Edge("A", "B")
            .Edge("B", "C");
        ExpansionResult r = await Expand(g, ExpandReq(g, [g.Seed("A")]));
        Assert.Equal(0.25, ScoreOf(r, g.N("C")), _eps); // 1*.5*1*.5 beats 1*.2*.5
        GraphPath? p = r.TracePath(g.N("C"));
        Assert.NotNull(p);
        Assert.Equal(new[] { g.N("A"), g.N("B"), g.N("C") }, p.Nodes);
        Assert.Equal(2, p.Kinds.Count);
    }

    [Fact]
    public async Task A_later_seed_with_a_higher_score_can_beat_a_nearer_one()
    {
        var g = new TestGraph().Edge("A", "T").Edge("B", "M").Edge("M", "T");
        ExpansionResult r = await Expand(g, ExpandReq(g, [g.Seed("A", 0.1), g.Seed("B", 1.0)]));
        Assert.Equal(0.25, ScoreOf(r, g.N("T")), _eps);
        Assert.Equal(new[] { g.N("B"), g.N("M"), g.N("T") }, r.TracePath(g.N("T"))!.Nodes);
    }

    [Fact]
    public async Task Cycles_terminate_and_never_change_a_seed_score()
    {
        var g = new TestGraph().Edge("A", "B").Edge("B", "C").Edge("C", "A").Edge("C", "C");
        ExpansionResult r = await Expand(g, ExpandReq(g, [g.Seed("A")], 10));
        Assert.Equal(3, r.Nodes.Count);
        Assert.Equal(1.0, ScoreOf(r, g.N("A")), _eps);
        Assert.Equal(0.5, ScoreOf(r, g.N("B")), _eps);
        Assert.Equal(0.25, ScoreOf(r, g.N("C")), _eps);
    }

    [Fact]
    public async Task Disconnected_seeds_return_only_themselves()
    {
        var g = new TestGraph().Edge("A", "B").Node("X").Node("Y");
        ExpansionResult r = await Expand(g, ExpandReq(g, [g.Seed("X", 0.7), g.Seed("Y", 0.3)]));
        Assert.Equal(2, r.Nodes.Count);
        Assert.Equal(0.7, ScoreOf(r, g.N("X")), _eps);
        Assert.Null(r.TracePath(g.N("A")));
        Assert.Equal([g.N("X")], r.TracePath(g.N("X"))!.Nodes);
    }

    [Fact]
    public async Task Duplicate_seeds_keep_the_largest_score()
    {
        var g = new TestGraph().Edge("A", "B");
        ExpansionResult r = await Expand(g, ExpandReq(g, [g.Seed("A", 0.2), g.Seed("A", 0.8)], 1));
        Assert.Equal(0.8, ScoreOf(r, g.N("A")), _eps);
        Assert.Equal(0.4, ScoreOf(r, g.N("B")), _eps);
    }

    [Fact]
    public async Task Mask_blocks_seeds_hops_and_paths_through_hidden_nodes()
    {
        var g = new TestGraph().Edge("A", "H").Edge("H", "C").Edge("A", "B");
        ExpansionResult r = await Expand(g, ExpandReq(g, [g.Seed("A")]) with { Mask = g.Hide("H") });
        Assert.False(r.TryGet(g.N("H"), out _));
        Assert.False(r.TryGet(g.N("C"), out _));
        Assert.True(r.TryGet(g.N("B"), out _));

        ExpansionResult hiddenSeed = await Expand(g, ExpandReq(g, [g.Seed("H")]) with { Mask = g.Hide("H") });
        Assert.Empty(hiddenSeed.Nodes);
    }

    [Fact]
    public async Task Per_node_edge_cap_keeps_the_heaviest_then_the_lowest_id()
    {
        var g = new TestGraph()
            .Edge("A", "B", GraphEdgeKind.Link, 0.5)
            .Edge("A", "C", GraphEdgeKind.Link, 0.9)
            .Edge("A", "D", GraphEdgeKind.Link, 0.9)
            .Edge("A", "E", GraphEdgeKind.Link, 0.1);
        ExpansionResult r = await Expand(g, ExpandReq(g, [g.Seed("A")], 1) with { MaxEdgesPerNode = 2 });
        Assert.Equal(new[] { g.N("A"), g.N("C"), g.N("D") }.Order(), r.Nodes.Select(n => n.Node).Order());
    }

    [Fact]
    public async Task Reached_cap_admits_the_best_new_nodes_first()
    {
        var g = new TestGraph()
            .Edge("A", "B", GraphEdgeKind.Link, 0.2)
            .Edge("A", "C", GraphEdgeKind.Link, 0.9)
            .Edge("A", "D", GraphEdgeKind.Link, 0.5);
        ExpansionResult r = await Expand(g, ExpandReq(g, [g.Seed("A")], 1) with { MaxReached = 3 });
        Assert.Equal(new[] { g.N("A"), g.N("C"), g.N("D") }.Order(), r.Nodes.Select(n => n.Node).Order());
    }

    [Fact]
    public async Task Equal_scores_are_ordered_by_node_id()
    {
        var g = new TestGraph().Edge("A", "D").Edge("A", "B").Edge("A", "C");
        ExpansionResult r = await Expand(g, ExpandReq(g, [g.Seed("A")], 1));
        Assert.Equal(
            new[] { g.N("A"), g.N("B"), g.N("C"), g.N("D") },
            r.Nodes.Select(n => n.Node));
    }

    [Fact]
    public async Task Predecessor_ties_resolve_to_the_lowest_node()
    {
        var g = new TestGraph().Edge("A", "Z").Edge("A", "M").Edge("M", "T").Edge("Z", "T");
        ExpansionResult r = await Expand(g, ExpandReq(g, [g.Seed("A")]));
        Assert.Equal(g.N("M"), r.Nodes.Single(n => n.Node == g.N("T")).Predecessor);
    }

    [Fact]
    public async Task Expansion_matches_the_oracle_on_random_graphs()
    {
        foreach (int seed in new[] { 1, 2, 3 })
        {
            GraphSnapshot snap = TestGraph.Random(300, 4, seed);
            SeedScore[] seeds = [new(3, 1.0), new(77, 0.6), new(250, 0.3)];
            foreach (EdgeDirection dir in Enum.GetValues<EdgeDirection>())
            {
                ExpansionResult r = await Ok(CreateKernel(4).TryExpandAsync(
                    new ExpansionRequest { Snapshot = snap, Seeds = seeds, MaxHops = 4, Direction = dir }, _ct));
                var expected = Oracle.Expand(snap, seeds, 4, 0.5, dir, 0.5);
                Assert.Equal(expected.Count, r.Nodes.Count);
                foreach ((int node, (double score, int level)) in expected)
                {
                    Assert.True(r.TryGet(node, out ReachedNode got));
                    Assert.Equal(score, got.Score, _eps);
                    Assert.Equal(level, got.Level);
                }
            }
        }
    }

    [Fact]
    public async Task Expansion_is_identical_for_one_and_many_threads()
    {
        GraphSnapshot snap = TestGraph.Random(5000, 6, 11);
        SeedScore[] seeds = [.. Enumerable.Range(0, 20).Select(i => new SeedScore(i * 211 % 5000, 1.0 - (i * 0.03)))];
        var req = new ExpansionRequest { Snapshot = snap, Seeds = seeds, MaxHops = 4, Direction = EdgeDirection.Both };
        ExpansionResult one = await Ok(CreateKernel(1).TryExpandAsync(req, _ct));
        foreach (int threads in new[] { 2, 4, 16 })
        {
            ExpansionResult many = await Ok(CreateKernel(threads).TryExpandAsync(req, _ct));
            Assert.Equal(one.Nodes, many.Nodes);
            foreach (ReachedNode n in one.Nodes.Take(200))
            {
                Assert.Equal(one.TracePath(n.Node)!.Nodes, many.TracePath(n.Node)!.Nodes);
            }
        }
    }

    // ---------------------------------------------------------------- closure

    private Task<ClosureResult> Close(TestGraph g, ClosureRequest r, int threads = 1) => Ok(CreateKernel(threads).TryClosureAsync(r, _ct));

    [Fact]
    public async Task Closure_follows_ancestors_along_dag_kinds_only()
    {
        // C built on B built on A; D linked (not a DAG kind).
        var g = new TestGraph()
            .Edge("C", "B", GraphEdgeKind.DerivedFrom)
            .Edge("B", "A", GraphEdgeKind.Supersedes)
            .Edge("C", "D", GraphEdgeKind.Link);
        ClosureResult r = await Close(g, new ClosureRequest { Snapshot = g.Snapshot, Roots = [g.N("C")] });
        Assert.Equal(new[] { g.N("C"), g.N("B"), g.N("A") }, r.Nodes.Select(n => n.Node));
        Assert.Equal(new[] { 0, 1, 2 }, r.Nodes.Select(n => n.Depth));
        Assert.Equal(GraphEdgeKind.Supersedes, r.Nodes[2].PredecessorKind);
        Assert.Empty(r.CycleNodes);
    }

    [Fact]
    public async Task Closure_descendants_follow_reverse_edges()
    {
        var g = new TestGraph().Edge("C", "B", GraphEdgeKind.DerivedFrom).Edge("B", "A", GraphEdgeKind.DerivedFrom);
        ClosureResult r = await Close(g, new ClosureRequest { Snapshot = g.Snapshot, Roots = [g.N("A")], Direction = EdgeDirection.Incoming });
        Assert.Equal(new[] { g.N("A"), g.N("B"), g.N("C") }.Length, r.Nodes.Count);
        Assert.Equal(2, r.Nodes.Single(n => n.Node == g.N("C")).Depth);
    }

    [Fact]
    public async Task Closure_respects_depth_and_node_limits()
    {
        var g = new TestGraph().Edge("A", "B", GraphEdgeKind.DerivedFrom).Edge("B", "C", GraphEdgeKind.DerivedFrom).Edge("C", "D", GraphEdgeKind.DerivedFrom);
        ClosureResult depth1 = await Close(g, new ClosureRequest { Snapshot = g.Snapshot, Roots = [g.N("A")], MaxDepth = 1 });
        Assert.Equal(2, depth1.Nodes.Count);
        ClosureResult depth0 = await Close(g, new ClosureRequest { Snapshot = g.Snapshot, Roots = [g.N("A")], MaxDepth = 0 });
        Assert.Single(depth0.Nodes);
        ClosureResult capped = await Close(g, new ClosureRequest { Snapshot = g.Snapshot, Roots = [g.N("A")], MaxNodes = 3 });
        Assert.Equal(3, capped.Nodes.Count);
    }

    [Fact]
    public async Task Closure_reports_cycles_and_terminates()
    {
        var g = new TestGraph()
            .Edge("A", "B", GraphEdgeKind.Supersedes)
            .Edge("B", "C", GraphEdgeKind.Supersedes)
            .Edge("C", "A", GraphEdgeKind.Supersedes)
            .Edge("C", "T", GraphEdgeKind.Supersedes);
        ClosureResult r = await Close(g, new ClosureRequest { Snapshot = g.Snapshot, Roots = [g.N("A")] });
        Assert.Equal(4, r.Nodes.Count);
        Assert.Equal(new[] { g.N("A"), g.N("B"), g.N("C") }, r.CycleNodes);
    }

    [Fact]
    public async Task Closure_reports_self_loops_as_cycles()
    {
        var g = new TestGraph().Edge("A", "A", GraphEdgeKind.DerivedFrom).Edge("A", "B", GraphEdgeKind.DerivedFrom);
        ClosureResult r = await Close(g, new ClosureRequest { Snapshot = g.Snapshot, Roots = [g.N("A")] });
        Assert.Equal(new[] { g.N("A") }, r.CycleNodes);
    }

    [Fact]
    public async Task Closure_diamond_uses_the_shortest_depth_and_lowest_predecessor()
    {
        var g = new TestGraph()
            .Edge("R", "X", GraphEdgeKind.DerivedFrom).Edge("R", "Y", GraphEdgeKind.DerivedFrom)
            .Edge("X", "T", GraphEdgeKind.DerivedFrom).Edge("Y", "T", GraphEdgeKind.DerivedFrom)
            .Edge("R", "T", GraphEdgeKind.DerivedFrom).Edge("Y", "U", GraphEdgeKind.DerivedFrom).Edge("U", "T", GraphEdgeKind.DerivedFrom);
        ClosureResult r = await Close(g, new ClosureRequest { Snapshot = g.Snapshot, Roots = [g.N("R")] });
        ClosureNode t = r.Nodes.Single(n => n.Node == g.N("T"));
        Assert.Equal(1, t.Depth);
        Assert.Equal(g.N("R"), t.Predecessor);
        Assert.Empty(r.CycleNodes);
    }

    [Fact]
    public async Task Closure_does_not_walk_through_hidden_nodes()
    {
        var g = new TestGraph().Edge("A", "H", GraphEdgeKind.DerivedFrom).Edge("H", "C", GraphEdgeKind.DerivedFrom).Edge("A", "B", GraphEdgeKind.DerivedFrom);
        ClosureResult r = await Close(g, new ClosureRequest { Snapshot = g.Snapshot, Roots = [g.N("A")], Mask = g.Hide("H") });
        Assert.Equal(new[] { g.N("A"), g.N("B") }, r.Nodes.Select(n => n.Node));
        ClosureResult hiddenRoot = await Close(g, new ClosureRequest { Snapshot = g.Snapshot, Roots = [g.N("H")], Mask = g.Hide("H") });
        Assert.Empty(hiddenRoot.Nodes);
    }

    [Fact]
    public async Task Closure_ignores_edge_weight_and_accepts_custom_kinds_and_many_roots()
    {
        var g = new TestGraph().Edge("A", "B", GraphEdgeKind.Affects, 0.0).Edge("X", "Y", GraphEdgeKind.Affects);
        ClosureResult r = await Close(g, new ClosureRequest
        {
            Snapshot = g.Snapshot,
            Roots = [g.N("X"), g.N("A"), g.N("A")],
            Kinds = EdgeKindSet.Of(GraphEdgeKind.Affects),
        });
        Assert.Equal(4, r.Nodes.Count);
        Assert.Equal(2, r.Nodes.Count(n => n.Depth == 0));
    }

    [Fact]
    public async Task Closure_on_an_empty_graph_or_with_no_roots_is_empty_and_bad_roots_decline()
    {
        var g = new TestGraph();
        Assert.Empty((await Close(g, new ClosureRequest { Snapshot = g.Snapshot, Roots = [] })).Nodes);
        var g2 = new TestGraph().Edge("A", "B");
        KernelResult<ClosureResult> bad = await CreateKernel(1).TryClosureAsync(new ClosureRequest { Snapshot = g2.Snapshot, Roots = [9] }, _ct);
        Assert.Equal(DeclineReason.InvalidRequest, bad.Reason);
        KernelResult<ClosureResult> neg = await CreateKernel(1).TryClosureAsync(new ClosureRequest { Snapshot = g2.Snapshot, Roots = [0], MaxDepth = -1 }, _ct);
        Assert.Equal(DeclineReason.InvalidRequest, neg.Reason);
    }

    [Fact]
    public async Task Closure_is_identical_for_one_and_many_threads()
    {
        GraphSnapshot snap = TestGraph.Random(5000, 5, 5);
        var req = new ClosureRequest { Snapshot = snap, Roots = [1, 50, 900], Direction = EdgeDirection.Both, MaxDepth = 6 };
        ClosureResult one = await Ok(CreateKernel(1).TryClosureAsync(req, _ct));
        ClosureResult many = await Ok(CreateKernel(8).TryClosureAsync(req, _ct));
        Assert.Equal(one.Nodes, many.Nodes);
        Assert.Equal(one.CycleNodes, many.CycleNodes);
    }

    // ---------------------------------------------------------------- personalised pagerank

    private Task<NodeScores> Rank(RankRequest r, int threads = 1) => Ok(CreateKernel(threads).TryRankAsync(r, _ct));

    [Fact]
    public async Task Ppr_over_an_empty_graph_or_without_positive_seeds_is_empty()
    {
        var g = new TestGraph();
        NodeScores r = await Rank(new RankRequest { Snapshot = g.Snapshot, Seeds = [] });
        Assert.Empty(r.Scores);
        var g2 = new TestGraph().Edge("A", "B");
        NodeScores zero = await Rank(new RankRequest { Snapshot = g2.Snapshot, Seeds = [g2.Seed("A", 0)] });
        Assert.Empty(zero.Scores);
    }

    [Fact]
    public async Task Ppr_of_an_isolated_seed_is_the_whole_mass()
    {
        var g = new TestGraph().Node("A").Edge("B", "C");
        NodeScores r = await Rank(new RankRequest { Snapshot = g.Snapshot, Seeds = [g.Seed("A", 3)] });
        Assert.Single(r.Scores);
        Assert.Equal(1.0, r.Scores[0].Score, _eps);
    }

    [Fact]
    public async Task Ppr_matches_the_closed_form_for_a_dangling_pair()
    {
        // A -> B, B dangling (its mass restarts at A). x_A = a / (1 - (1-a)^2), x_B = (1-a) x_A.
        var g = new TestGraph().Edge("A", "B");
        NodeScores r = await Rank(new RankRequest { Snapshot = g.Snapshot, Seeds = [g.Seed("A")], Iterations = 200, Tolerance = 1e-12 });
        double a = 0.15;
        double xa = a / (1 - ((1 - a) * (1 - a)));
        Assert.Equal(xa, r.Scores.Single(s => s.Node == g.N("A")).Score, 1e-9);
        Assert.Equal((1 - a) * xa, r.Scores.Single(s => s.Node == g.N("B")).Score, 1e-9);
        Assert.True(r.Converged);
        Assert.Equal(1.0, r.Scores.Sum(s => s.Score), 1e-9);
    }

    [Fact]
    public async Task Ppr_splits_mass_by_edge_weight()
    {
        var g = new TestGraph().Edge("A", "B", GraphEdgeKind.Link, 3.0).Edge("A", "C", GraphEdgeKind.Link, 1.0);
        NodeScores r = await Rank(new RankRequest { Snapshot = g.Snapshot, Seeds = [g.Seed("A")] });
        double b = r.Scores.Single(s => s.Node == g.N("B")).Score;
        double c = r.Scores.Single(s => s.Node == g.N("C")).Score;
        Assert.Equal(3.0, b / c, 1e-9);
    }

    [Fact]
    public async Task Ppr_rewards_nodes_reachable_from_many_seeds()
    {
        var g = new TestGraph().Edge("S1", "Hub").Edge("S2", "Hub").Edge("S1", "Lone");
        NodeScores r = await Rank(new RankRequest { Snapshot = g.Snapshot, Seeds = [g.Seed("S1"), g.Seed("S2")] });
        Assert.True(r.Scores.Single(s => s.Node == g.N("Hub")).Score > r.Scores.Single(s => s.Node == g.N("Lone")).Score);
    }

    [Fact]
    public async Task Ppr_stops_at_the_iteration_limit_and_reports_non_convergence()
    {
        GraphSnapshot snap = TestGraph.Random(100, 3, 4);
        NodeScores r = await Rank(new RankRequest { Snapshot = snap, Seeds = [new SeedScore(0, 1)], Iterations = 3, Tolerance = 0 });
        Assert.Equal(3, r.Iterations);
        Assert.False(r.Converged);
        Assert.True(r.FinalDelta > 0);
    }

    [Fact]
    public async Task Ppr_stops_early_once_the_change_is_below_tolerance()
    {
        GraphSnapshot snap = TestGraph.Random(100, 3, 4);
        NodeScores r = await Rank(new RankRequest { Snapshot = snap, Seeds = [new SeedScore(0, 1)], Iterations = 100, Tolerance = 1e-3 });
        Assert.True(r.Converged);
        Assert.InRange(r.Iterations, 1, 99);
        Assert.True(r.FinalDelta < 1e-3);
    }

    [Fact]
    public async Task Ppr_never_routes_mass_through_a_hidden_node()
    {
        var g = new TestGraph().Edge("A", "H").Edge("H", "C").Edge("A", "B");
        NodeScores r = await Rank(new RankRequest { Snapshot = g.Snapshot, Seeds = [g.Seed("A")], Mask = g.Hide("H") });
        Assert.DoesNotContain(r.Scores, s => s.Node == g.N("H") || s.Node == g.N("C"));
        Assert.Equal(1.0, r.Scores.Sum(s => s.Score), 1e-6);
    }

    [Fact]
    public async Task Ppr_can_be_restricted_to_a_subgraph()
    {
        var g = new TestGraph().Edge("A", "B").Edge("B", "C");
        NodeMask sub = NodeMask.FromNodes(g.Snapshot.NodeCount, [g.N("A"), g.N("B")]);
        NodeScores r = await Rank(new RankRequest { Snapshot = g.Snapshot, Seeds = [g.Seed("A")], Subgraph = sub });
        Assert.DoesNotContain(r.Scores, s => s.Node == g.N("C"));
    }

    [Fact]
    public async Task Ppr_ignores_zero_weight_edges_and_orders_ties_by_node()
    {
        var g = new TestGraph().Edge("A", "Z", GraphEdgeKind.Link, 0.0).Edge("A", "C").Edge("A", "B");
        NodeScores r = await Rank(new RankRequest { Snapshot = g.Snapshot, Seeds = [g.Seed("A")] });
        Assert.DoesNotContain(r.Scores, s => s.Node == g.N("Z"));
        int ib = r.Scores.Select((s, i) => (s, i)).Single(t => t.s.Node == g.N("B")).i;
        int ic = r.Scores.Select((s, i) => (s, i)).Single(t => t.s.Node == g.N("C")).i;
        Assert.True(ib < ic);
        Assert.Equal(r.Scores[ib].Score, r.Scores[ic].Score, 0);
    }

    [Fact]
    public async Task Ppr_limit_keeps_the_top_scores()
    {
        var g = new TestGraph().Edge("A", "B").Edge("A", "C").Edge("A", "D");
        NodeScores r = await Rank(new RankRequest { Snapshot = g.Snapshot, Seeds = [g.Seed("A")], Limit = 2 });
        Assert.Equal(2, r.Scores.Count);
        Assert.Equal(g.N("A"), r.Scores[0].Node);
    }

    [Fact]
    public async Task Ppr_declines_bad_parameters()
    {
        var g = new TestGraph().Edge("A", "B");
        IGraphKernel k = CreateKernel(1);
        RankRequest ok = new() { Snapshot = g.Snapshot, Seeds = [g.Seed("A")] };
        Assert.Equal(DeclineReason.InvalidRequest, (await k.TryRankAsync(ok with { Alpha = 0 }, _ct)).Reason);
        Assert.Equal(DeclineReason.InvalidRequest, (await k.TryRankAsync(ok with { Alpha = 1 }, _ct)).Reason);
        Assert.Equal(DeclineReason.InvalidRequest, (await k.TryRankAsync(ok with { Iterations = 0 }, _ct)).Reason);
        Assert.Equal(DeclineReason.InvalidRequest, (await k.TryRankAsync(ok with { Seeds = [new SeedScore(5, 1)] }, _ct)).Reason);
    }

    [Fact]
    public async Task Ppr_matches_the_oracle_on_random_graphs()
    {
        foreach (int seed in new[] { 21, 22 })
        {
            GraphSnapshot snap = TestGraph.Random(400, 4, seed);
            SeedScore[] seeds = [new(5, 1.0), new(300, 2.0)];
            foreach (EdgeDirection dir in Enum.GetValues<EdgeDirection>())
            {
                NodeScores r = await Rank(new RankRequest { Snapshot = snap, Seeds = seeds, Direction = dir, Iterations = 20, Tolerance = 0 }, 4);
                double[] expected = Oracle.Ppr(snap, seeds, 0.15, 20, dir, 0.5);
                foreach (NodeScore s in r.Scores)
                {
                    Assert.Equal(expected[s.Node], s.Score, 1e-12);
                }

                // Nodes the walk cannot reach hold no mass in the oracle either.
                Assert.Equal(expected.Count(v => v > 0), r.Scores.Count);
            }
        }
    }

    [Fact]
    public async Task Ppr_is_identical_for_one_and_many_threads()
    {
        GraphSnapshot snap = TestGraph.Random(20000, 5, 9);
        var req = new RankRequest { Snapshot = snap, Seeds = [new SeedScore(1, 1), new SeedScore(5000, 0.5)], Direction = EdgeDirection.Both };
        NodeScores one = await Rank(req, 1);
        foreach (int threads in new[] { 2, 8, 32 })
        {
            NodeScores many = await Rank(req, threads);
            Assert.Equal(one.Iterations, many.Iterations);
            Assert.Equal(one.Scores, many.Scores); // bit-identical
        }
    }

    // ---------------------------------------------------------------- subgraph assembly

    private Task<SubgraphResult> Assemble(SubgraphRequest r, int threads = 1) => Ok(CreateKernel(threads).TryAssembleSubgraphAsync(r, _ct));

    [Fact]
    public async Task Subgraph_ranks_nodes_and_explains_them_with_paths()
    {
        var g = new TestGraph().Edge("A", "B").Edge("B", "C").Edge("A", "D", GraphEdgeKind.Supersedes);
        SubgraphResult r = await Assemble(new SubgraphRequest { Snapshot = g.Snapshot, Seeds = [g.Seed("A")] });
        Assert.Equal(new[] { g.N("A"), g.N("B"), g.N("C"), g.N("D") }, r.Nodes.Select(n => n.Node));
        Assert.Equal(new[] { 0, 1, 2, 3 }, r.Nodes.Select(n => n.Rank));
        Assert.Equal(NodeOrigin.Seed, r.Nodes[0].Origin);
        Assert.Equal(NodeOrigin.Expansion, r.Nodes[1].Origin);
        Assert.Equal(3, r.Paths.Count);
        SubgraphPath toC = r.Paths.Single(p => p.Node == g.N("C"));
        Assert.Equal(new[] { g.N("A"), g.N("B"), g.N("C") }, toC.Path.Nodes);
        Assert.DoesNotContain(r.Paths, p => p.Node == g.N("A"));
    }

    [Fact]
    public async Task Subgraph_node_cap_keeps_the_best_nodes()
    {
        var g = new TestGraph().Edge("A", "B").Edge("B", "C").Edge("C", "D");
        SubgraphResult r = await Assemble(new SubgraphRequest { Snapshot = g.Snapshot, Seeds = [g.Seed("A")], MaxHops = 3, NodeCap = 2 });
        Assert.Equal(new[] { g.N("A"), g.N("B") }, r.Nodes.Select(n => n.Node));
        Assert.Single(r.Paths);
        SubgraphResult zero = await Assemble(new SubgraphRequest { Snapshot = g.Snapshot, Seeds = [g.Seed("A")], NodeCap = 0 });
        Assert.Empty(zero.Nodes);
    }

    [Fact]
    public async Task Subgraph_with_no_seeds_or_an_empty_graph_is_empty()
    {
        var g = new TestGraph();
        SubgraphResult r = await Assemble(new SubgraphRequest { Snapshot = g.Snapshot, Seeds = [] });
        Assert.Empty(r.Nodes);
        Assert.Empty(r.Paths);
    }

    [Fact]
    public async Task Subgraph_closure_reserves_a_share_for_ancestry_and_reports_cycles()
    {
        // Seed S has many strong link neighbours plus a weakly connected decision chain S -> P1 -> P2 (-> P1: a cycle).
        var g = new TestGraph();
        for (int i = 0; i < 10; i++)
        {
            g.Edge("S", "L" + i, GraphEdgeKind.Link);
        }

        g.Edge("S", "P1", GraphEdgeKind.Supersedes, 0.01).Edge("P1", "P2", GraphEdgeKind.DerivedFrom, 0.01).Edge("P2", "P1", GraphEdgeKind.DerivedFrom, 0.01);
        var req = new SubgraphRequest
        {
            Snapshot = g.Snapshot,
            Seeds = [g.Seed("S")],
            NodeCap = 5,
            ClosureKinds = EdgeKindSet.Dag,
            ClosureShare = 0.4,
        };
        SubgraphResult withClosure = await Assemble(req);
        Assert.Equal(5, withClosure.Nodes.Count);
        Assert.Contains(withClosure.Nodes, n => n.Node == g.N("P1") && n.Origin.HasFlag(NodeOrigin.Closure));
        Assert.Contains(withClosure.Nodes, n => n.Node == g.N("P2"));
        Assert.Equal(new[] { g.N("P1"), g.N("P2") }, withClosure.CycleNodes);
        Assert.NotNull(withClosure.Paths.Single(p => p.Node == g.N("P2")).Path);

        SubgraphResult without = await Assemble(req with { ClosureKinds = null });
        Assert.DoesNotContain(without.Nodes, n => n.Node == g.N("P2"));
        Assert.Empty(without.CycleNodes);
    }

    [Fact]
    public async Task Subgraph_closure_only_nodes_get_closure_paths()
    {
        var g = new TestGraph().Edge("S", "D", GraphEdgeKind.DerivedFrom).Edge("D", "E", GraphEdgeKind.DerivedFrom);
        SubgraphResult r = await Assemble(new SubgraphRequest
        {
            Snapshot = g.Snapshot,
            Seeds = [g.Seed("S")],
            MaxHops = 0,
            ClosureKinds = EdgeKindSet.Dag,
        });
        Assert.Equal(3, r.Nodes.Count);
        Assert.Equal(new[] { g.N("S"), g.N("D"), g.N("E") }, r.Paths.Single(p => p.Node == g.N("E")).Path.Nodes);
        Assert.Equal(NodeOrigin.Closure, r.Nodes.Single(n => n.Node == g.N("E")).Origin);
    }

    [Fact]
    public async Task Subgraph_ppr_ranks_by_walk_probability()
    {
        var g = new TestGraph().Edge("S1", "Hub").Edge("S2", "Hub").Edge("S1", "Lone");
        SubgraphResult r = await Assemble(new SubgraphRequest
        {
            Snapshot = g.Snapshot,
            Seeds = [g.Seed("S1"), g.Seed("S2")],
            Method = RankMethod.Ppr,
        });
        Assert.Equal(RankMethod.Ppr, r.Method);
        Assert.True(r.PprIterations > 0);
        int hub = r.Nodes.Single(n => n.Node == g.N("Hub")).Rank;
        int lone = r.Nodes.Single(n => n.Node == g.N("Lone")).Rank;
        Assert.True(hub < lone);
        Assert.Equal(1.0, r.Nodes.Sum(n => n.Score), 1e-6);
    }

    [Fact]
    public async Task Subgraph_never_contains_hidden_nodes()
    {
        var g = new TestGraph().Edge("A", "H", GraphEdgeKind.DerivedFrom).Edge("H", "C", GraphEdgeKind.DerivedFrom).Edge("A", "B");
        foreach (RankMethod method in Enum.GetValues<RankMethod>())
        {
            SubgraphResult r = await Assemble(new SubgraphRequest
            {
                Snapshot = g.Snapshot,
                Seeds = [g.Seed("A")],
                Mask = g.Hide("H"),
                ClosureKinds = EdgeKindSet.Dag,
                Method = method,
            });
            Assert.DoesNotContain(r.Nodes, n => n.Node == g.N("H") || n.Node == g.N("C"));
            Assert.All(r.Paths, p => Assert.DoesNotContain(g.N("H"), p.Path.Nodes));
        }
    }

    [Fact]
    public async Task Subgraph_declines_bad_parameters()
    {
        var g = new TestGraph().Edge("A", "B");
        IGraphKernel k = CreateKernel(1);
        var ok = new SubgraphRequest { Snapshot = g.Snapshot, Seeds = [g.Seed("A")] };
        Assert.Equal(DeclineReason.InvalidRequest, (await k.TryAssembleSubgraphAsync(ok with { NodeCap = -1 }, _ct)).Reason);
        Assert.Equal(DeclineReason.InvalidRequest, (await k.TryAssembleSubgraphAsync(ok with { ClosureShare = 2 }, _ct)).Reason);
        Assert.Equal(DeclineReason.InvalidRequest, (await k.TryAssembleSubgraphAsync(ok with { Seeds = [new SeedScore(8, 1)] }, _ct)).Reason);
    }

    [Fact]
    public async Task Subgraph_is_identical_for_one_and_many_threads()
    {
        GraphSnapshot snap = TestGraph.Random(8000, 5, 31);
        foreach (RankMethod method in Enum.GetValues<RankMethod>())
        {
            var req = new SubgraphRequest
            {
                Snapshot = snap,
                Seeds = [new SeedScore(10, 1), new SeedScore(4000, 0.7), new SeedScore(7000, 0.2)],
                MaxHops = 3,
                Direction = EdgeDirection.Both,
                Method = method,
                ClosureKinds = EdgeKindSet.Dag,
                NodeCap = 100,
            };
            SubgraphResult one = await Assemble(req, 1);
            SubgraphResult many = await Assemble(req, 16);
            Assert.Equal(one.Nodes, many.Nodes);
            Assert.Equal(one.CycleNodes, many.CycleNodes);
            Assert.Equal(one.Paths.Select(p => (p.Node, p.Path.Nodes.ToArray())), many.Paths.Select(p => (p.Node, p.Path.Nodes.ToArray())));
        }
    }
}
