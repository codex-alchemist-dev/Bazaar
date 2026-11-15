using System.Collections.Immutable;
using Bazaar.Contracts.Graph;
using Bazaar.RyuseiFuda.Roadmap;

namespace Bazaar.Server.Tests.Roadmap;

/// <summary>The roadmap as Constellation nodes and edges (doc 38, "Constellation"). Pure: no database.</summary>
public sealed class RoadmapGraphProjectionTests
{
    private static RoadmapView Sample(params string[] dependsOnForA) => RoadmapReadModel.Build(
    [
        new RoadmapDocumentInput("roadmap/phases/one.md", "One", "one", new RoadmapPhase(RoadmapStatus.Active)),
        new RoadmapDocumentInput("roadmap/objectives/a.md", "A", null,
            new RoadmapObjective("one", RoadmapStatus.Active, null, [.. dependsOnForA])),
        new RoadmapDocumentInput("roadmap/objectives/b.md", "B", null,
            new RoadmapObjective("one", RoadmapStatus.Planned, null, ImmutableArray<string>.Empty)),
        new RoadmapDocumentInput("roadmap/objectives/loose.md", "Loose", null,
            new RoadmapObjective("missing", RoadmapStatus.Planned, null, ImmutableArray<string>.Empty)),
    ], new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));

    [Fact]
    public void Nodes_are_phases_and_objectives_with_their_kind_in_the_id_prefix()
    {
        var graph = RoadmapGraphProjection.Build(Sample());

        Assert.Equal(
            ["objective:roadmap/objectives/a.md", "objective:roadmap/objectives/b.md", "objective:roadmap/objectives/loose.md", "phase:one"],
            graph.Nodes.Select(n => n.Id));
    }

    [Fact]
    public void Objectives_link_to_their_phase_and_to_the_objectives_they_depend_on()
    {
        var graph = RoadmapGraphProjection.Build(Sample("b"));

        Assert.Equal(
        [
            new GraphEdge("objective:roadmap/objectives/a.md", "objective:roadmap/objectives/b.md"),
            new GraphEdge("objective:roadmap/objectives/a.md", "phase:one"),
            new GraphEdge("objective:roadmap/objectives/b.md", "phase:one"),
        ], graph.Edges);
    }

    [Fact]
    public void Self_dependencies_and_unknown_dependencies_add_no_edge()
    {
        var graph = RoadmapGraphProjection.Build(Sample("a", "nowhere"));

        Assert.DoesNotContain(graph.Edges, e => e.From == e.To);
        // Only the edge to the phase remains for objective a.
        Assert.Equal(
            [new GraphEdge("objective:roadmap/objectives/a.md", "phase:one")],
            graph.Edges.Where(e => e.From == "objective:roadmap/objectives/a.md"));
    }

    [Fact]
    public void Version_is_stable_for_the_same_roadmap_and_changes_with_an_edge()
    {
        var first = RoadmapGraphProjection.Build(Sample("b")).GraphVersion;

        Assert.Equal(first, RoadmapGraphProjection.Build(Sample("b")).GraphVersion);
        Assert.NotEqual(first, RoadmapGraphProjection.Build(Sample()).GraphVersion);
    }
}
