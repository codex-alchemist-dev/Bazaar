using Bazaar.Constellation.Communities;
using Bazaar.Constellation.Communities.Abstractions;
using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Sections.Tests;

public class SectionsTests
{
    private static (List<CommunityNode> Nodes, List<CommunityEdge> Edges) TwoCliques()
    {
        var nodes = new List<CommunityNode>();
        var edges = new List<CommunityEdge>();
        foreach (var p in new[] { "a", "b" })
        {
            for (var i = 0; i < 6; i++)
            {
                nodes.Add(new CommunityNode($"{p}{i}"));
                for (var j = 0; j < i; j++)
                {
                    edges.Add(new CommunityEdge($"{p}{i}", $"{p}{j}"));
                }
            }
        }

        return (nodes, edges);
    }

    [Fact]
    public async Task Layout_feeds_the_renderer_section_model_with_nothing_lost()
    {
        var (nodes, edges) = TwoCliques();
        var layout = await new CommunityLayout().ComputeAsync(nodes, edges, null, TestContext.Current.CancellationToken);

        var defs = CommunitySectionAdapter.ToSectionDefs(layout);
        var positions = CommunitySectionAdapter.PositionsById(layout);

        Assert.Equal(2, defs.Count);
        Assert.Equal(12, defs.Sum(d => d.Members.Count));
        Assert.All(defs, d => Assert.Null(d.ParentId));
        Assert.Equal(12, positions.Count);
        Assert.All(defs, d => Assert.All(d.Members, m => Assert.True(positions.ContainsKey(m))));
        Assert.NotNull(new SectionSet(defs));
    }

    private sealed class GatedLayout(Dictionary<long, TaskCompletionSource> gates) : ICommunityLayout
    {
        public async Task<CommunityLayoutResult> ComputeAsync(
            IReadOnlyList<CommunityNode> nodes, IReadOnlyList<CommunityEdge> edges, CommunityLayoutOptions? options, CancellationToken cancellationToken)
        {
            var version = nodes.Count;
            await gates[version].Task.WaitAsync(cancellationToken);
            return new CommunityLayoutResult([], ReadOnlyMemory<float>.Empty, [], version);
        }
    }

    private static List<CommunityNode> Nodes(int n) => Enumerable.Range(0, n).Select(i => new CommunityNode($"n{i}")).ToList();

    [Fact]
    public async Task A_newer_request_cancels_the_older_and_only_the_newest_is_published()
    {
        var gates = new Dictionary<long, TaskCompletionSource> { [1] = new(), [2] = new() };
        using var publisher = new SectionLayoutPublisher(new GatedLayout(gates));
        var published = new List<long>();
        publisher.Published += (v, _) => published.Add(v);

        var first = publisher.RequestAsync(1, Nodes(1), []);
        var second = publisher.RequestAsync(2, Nodes(2), []);
        gates[2].SetResult();
        await second;
        gates[1].SetResult();
        await first;

        Assert.Equal([2L], published);
        Assert.Equal(2L, publisher.Latest!.Value.GraphVersion);
    }

    [Fact]
    public async Task A_stale_version_is_ignored()
    {
        var gates = new Dictionary<long, TaskCompletionSource> { [1] = new(), [3] = new() };
        using var publisher = new SectionLayoutPublisher(new GatedLayout(gates));
        gates[3].SetResult();
        await publisher.RequestAsync(5, Nodes(3), []);

        await publisher.RequestAsync(4, Nodes(1), []); // would block if it ran: gate 1 never opens

        Assert.Equal(5L, publisher.Latest!.Value.GraphVersion);
    }
}
