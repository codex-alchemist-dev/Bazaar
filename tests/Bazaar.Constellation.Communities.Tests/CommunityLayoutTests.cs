using System.Diagnostics;
using Bazaar.Constellation.Communities.Abstractions;

namespace Bazaar.Constellation.Communities.Tests;

internal static class LayoutExtensions
{
    public static Task<CommunityLayoutResult> Compute(
        this ICommunityLayout layout, IReadOnlyList<CommunityNode> nodes, IReadOnlyList<CommunityEdge> edges, CommunityLayoutOptions? options = null) =>
        layout.ComputeAsync(nodes, edges, options, TestContext.Current.CancellationToken);
}

public class CommunityLayoutTests
{

    private static (List<CommunityNode> Nodes, List<CommunityEdge> Edges) Cliques(int count, int size, bool bridge = true)
    {
        var nodes = new List<CommunityNode>();
        var edges = new List<CommunityEdge>();
        for (var c = 0; c < count; c++)
        {
            for (var i = 0; i < size; i++)
            {
                nodes.Add(new CommunityNode($"c{c:D3}n{i:D3}", i == 0 ? $"Hub {c}" : null));
                for (var j = 0; j < i; j++)
                {
                    edges.Add(new CommunityEdge($"c{c:D3}n{i:D3}", $"c{c:D3}n{j:D3}"));
                }
            }

            if (bridge && c > 0)
            {
                edges.Add(new CommunityEdge($"c{c:D3}n000", $"c{c - 1:D3}n000"));
            }
        }

        return (nodes, edges);
    }

    private static float[] PositionOf(CommunityLayoutResult r, string id)
    {
        var i = r.NodeIds.ToList().IndexOf(id);
        var xy = r.Xy.Span;
        return [xy[i * 2], xy[(i * 2) + 1]];
    }

    [Fact]
    public async Task Fixture_graph_gives_one_labelled_section_per_clique()
    {
        var (nodes, edges) = Cliques(4, 8);
        var result = await new CommunityLayout().Compute(nodes, edges);

        Assert.Equal(4, result.Sections.Count);
        Assert.All(result.Sections, s => Assert.Equal(8, s.Members.Count));
        Assert.All(result.Sections, s => Assert.Equal(8, s.Members.Select(m => m[..4]).Distinct().Count() * 8));
        Assert.All(result.Sections, s => Assert.StartsWith("Hub ", s.Label));
        Assert.All(result.Sections, s => Assert.EndsWith("(8)", s.Label));
        Assert.Equal(4, result.Sections.Select(s => s.Color).Distinct().Count());
        Assert.True(result.Modularity > 0.5);
        Assert.Equal(nodes.Count * 2, result.Xy.Length);
    }

    [Fact]
    public async Task Members_sit_inside_their_section_disc_and_discs_do_not_overlap()
    {
        var (nodes, edges) = Cliques(10, 12);
        var result = await new CommunityLayout().Compute(nodes, edges);

        foreach (var s in result.Sections)
        {
            foreach (var m in s.Members)
            {
                var p = PositionOf(result, m);
                var d = Math.Sqrt(Math.Pow(p[0] - s.CenterX, 2) + Math.Pow(p[1] - s.CenterY, 2));
                Assert.True(d <= s.Radius + 0.01, $"{m} is {d} from its centre, radius {s.Radius}");
            }
        }

        for (var a = 0; a < result.Sections.Count; a++)
        {
            for (var b = a + 1; b < result.Sections.Count; b++)
            {
                var x = result.Sections[a];
                var y = result.Sections[b];
                var d = Math.Sqrt(Math.Pow(x.CenterX - y.CenterX, 2) + Math.Pow(x.CenterY - y.CenterY, 2));
                Assert.True(d >= x.Radius + y.Radius, $"sections {x.Id} and {y.Id} overlap");
            }
        }
    }

    [Fact]
    public async Task Same_input_gives_the_same_layout_in_any_input_order()
    {
        var (nodes, edges) = Cliques(5, 6);
        var layout = new CommunityLayout();
        var a = await layout.Compute(nodes, edges);
        var b = await layout.Compute([.. nodes.AsEnumerable().Reverse()], [.. edges.AsEnumerable().Reverse()]);

        Assert.Equal(a.NodeIds, b.NodeIds);
        Assert.Equal(a.Xy.ToArray(), b.Xy.ToArray());
        Assert.Equal(a.Sections.Select(s => s.Label), b.Sections.Select(s => s.Label));
    }

    [Fact]
    public async Task Best_connected_node_sits_at_the_community_centre()
    {
        var nodes = new[] { "a", "b", "c", "d", "hub" }.Select(i => new CommunityNode(i)).ToList();
        var edges = new[] { "a", "b", "c", "d" }.Select(i => new CommunityEdge("hub", i)).ToList();
        var result = await new CommunityLayout().Compute(nodes, edges);

        var s = Assert.Single(result.Sections);
        Assert.Equal("hub (5)", s.Label);
        var p = PositionOf(result, "hub");
        Assert.True(Math.Abs(p[0] - s.CenterX) < 15 && Math.Abs(p[1] - s.CenterY) < 15);
    }

    [Fact]
    public async Task Section_count_is_capped_and_the_tail_merges_into_other()
    {
        var (nodes, edges) = Cliques(200, 3, bridge: false);
        var result = await new CommunityLayout().Compute(nodes, edges, new CommunityLayoutOptions { MaxSections = 64 });

        Assert.Equal(64, result.Sections.Count);
        var other = result.Sections[^1];
        Assert.Equal("community:other", other.Id);
        Assert.Equal(137 * 3, other.Members.Count);
        Assert.Equal(nodes.Count, result.Sections.Sum(s => s.Members.Count));
    }

    [Fact]
    public async Task Render_budget_holds_for_a_large_fixture()
    {
        // 30 communities x 700 nodes, sparse random edges inside, a few across: 21k nodes, ~100k edges.
        var rng = new Random(7);
        var nodes = new List<CommunityNode>();
        var edges = new List<CommunityEdge>();
        for (var c = 0; c < 30; c++)
        {
            for (var i = 0; i < 700; i++)
            {
                nodes.Add(new CommunityNode($"c{c:D2}n{i:D3}"));
            }

            for (var e = 0; e < 3500; e++)
            {
                var a = rng.Next(700);
                var b = rng.Next(700);
                if (a != b)
                {
                    edges.Add(new CommunityEdge($"c{c:D2}n{a:D3}", $"c{c:D2}n{b:D3}"));
                }
            }

            if (c > 0)
            {
                edges.Add(new CommunityEdge($"c{c:D2}n000", $"c{c - 1:D2}n000"));
            }
        }

        var clock = Stopwatch.StartNew();
        var result = await new CommunityLayout().Compute(nodes, edges);
        clock.Stop();

        // Section count is what the renderer pays for per frame (its cap is 64); 10 s is a loose guard for slow CI.
        Assert.InRange(result.Sections.Count, 20, 64);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"layout took {clock.Elapsed}");
    }

    private sealed class RecordingScheduler : TaskScheduler
    {
        public int Queued;

        protected override void QueueTask(Task task)
        {
            Interlocked.Increment(ref Queued);
            ThreadPool.QueueUserWorkItem(_ => TryExecuteTask(task));
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks() => [];
    }

    [Fact]
    public async Task Work_is_queued_to_the_scheduler_and_never_runs_inline()
    {
        var scheduler = new RecordingScheduler();
        var (nodes, edges) = Cliques(2, 4);

        await new CommunityLayout(scheduler).Compute(nodes, edges);

        Assert.Equal(1, scheduler.Queued);
    }

    [Fact]
    public async Task Cancellation_before_start_cancels_the_task()
    {
        var (nodes, edges) = Cliques(2, 4);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new CommunityLayout().ComputeAsync(nodes, edges, null, cts.Token));
    }

    [Fact]
    public async Task Empty_graph_gives_an_empty_result()
    {
        var result = await new CommunityLayout().Compute([], []);
        Assert.Empty(result.Sections);
        Assert.Empty(result.NodeIds);
    }

    [Fact]
    public async Task Edge_to_an_unknown_node_is_rejected()
    {
        var nodes = new List<CommunityNode> { new("a") };
        await Assert.ThrowsAsync<ArgumentException>(
            () => new CommunityLayout().Compute(nodes, [new CommunityEdge("a", "ghost")]));
    }

    [Fact]
    public void Section_colours_are_opaque_and_distinct_for_the_first_sixteen_hues()
    {
        var colours = Enumerable.Range(0, 16).Select(CommunityLayout.GoldenColor).ToList();
        Assert.All(colours, c => Assert.Equal(0xFFu, c >> 24));
        Assert.Equal(16, colours.Distinct().Count());
    }
}
