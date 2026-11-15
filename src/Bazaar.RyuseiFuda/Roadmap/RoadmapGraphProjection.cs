using Bazaar.Contracts.Graph;
using Bazaar.Server.Constellation.Tiles;

namespace Bazaar.RyuseiFuda.Roadmap;

/// <summary>
/// The roadmap as a Constellation graph (doc 19, "Constellation" in doc 38). Phases and objectives are nodes; an objective has an
/// edge to its phase and an edge to each objective it depends on. Pure: no I/O.
/// </summary>
/// <remarks>
/// <see cref="GraphNode"/> carries only an id, so the node kind is the id prefix: <c>phase:&lt;slug&gt;</c> or
/// <c>objective:&lt;path&gt;</c>. Doc 19 will add typed node and edge kinds; until then clients read the prefix. Self-dependencies
/// and dependencies on unknown objectives add no edge, as in <see cref="VaultGraphProjection"/>.
/// </remarks>
public static class RoadmapGraphProjection
{
    public const string PhasePrefix = "phase:";
    public const string ObjectivePrefix = "objective:";

    public static GraphSnapshot Build(RoadmapView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        var objectives = view.Phases.SelectMany(p => p.Objectives)
            .Concat(view.UnphasedObjectives)
            .DistinctBy(o => o.Path)
            .OrderBy(o => o.Path, StringComparer.Ordinal)
            .ToList();
        var paths = objectives.Select(o => o.Path).ToList();

        var nodeIds = objectives.Select(o => ObjectivePrefix + o.Path)
            .Concat(view.Phases.Select(p => PhasePrefix + p.Slug))
            .Order(StringComparer.Ordinal)
            .ToList();

        var edges = new List<GraphEdge>();
        foreach (var objective in objectives)
        {
            var from = ObjectivePrefix + objective.Path;
            if (view.Phases.Any(p => p.Slug == objective.PhaseSlug))
                edges.Add(new GraphEdge(from, PhasePrefix + objective.PhaseSlug));

            foreach (var slug in objective.DependsOn)
            {
                var target = RoadmapReadModel.ResolveDependency(paths, slug);
                if (target is null || target == objective.Path)
                    continue;
                edges.Add(new GraphEdge(from, ObjectivePrefix + target));
            }
        }

        var sortedEdges = edges
            .Distinct()
            .OrderBy(e => e.From, StringComparer.Ordinal)
            .ThenBy(e => e.To, StringComparer.Ordinal)
            .ToList();

        return new GraphSnapshot(VaultGraphProjection.VersionOf(nodeIds, sortedEdges), nodeIds.Select(id => new GraphNode(id)).ToList(), sortedEdges);
    }
}
