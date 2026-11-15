using System.Collections.Immutable;

namespace Bazaar.RyuseiFuda.Roadmap;

/// <summary>
/// One parsed roadmap document with the Vault facts the read model needs. <see cref="Path"/> is the Vault path; for an
/// objective it is also the ref that task links use. <see cref="Slug"/> is the phase slug (the file name without .md) and is
/// required for phases; it is ignored for objectives.
/// </summary>
public sealed record RoadmapDocumentInput(string Path, string Title, string? Slug, RoadmapDocument Document);

/// <summary>The objective roadmap as derived from its documents. Nothing here is stored.</summary>
public sealed record RoadmapView(
    IReadOnlyList<RoadmapPhaseView> Phases,
    IReadOnlyList<RoadmapObjectiveView> UnphasedObjectives,
    IReadOnlyList<string> DependencyCycle);

/// <summary>One phase, with its gate and its objectives. Objectives are ordered by path (ordinal).</summary>
public sealed record RoadmapPhaseView(
    string Slug,
    string Path,
    string Title,
    bool GatePassed,
    int ObjectiveCount,
    int OpenObjectives,
    IReadOnlyList<RoadmapObjectiveView> Objectives);

/// <summary>One objective with its dependencies (bare slugs, as written) and its task progress.</summary>
public sealed record RoadmapObjectiveView(
    string Path,
    string Title,
    string PhaseSlug,
    RoadmapStatus Status,
    string? Owner,
    IReadOnlyList<string> DependsOn,
    RoadmapProgress Progress);

/// <summary>
/// Task progress for one objective. <see cref="DoneShare"/> is Done / LinkedTasks, and null when no task is linked.
/// </summary>
public sealed record RoadmapProgress(int LinkedTasks, int Backlog, int Ready, int InProgress, int Review, int Done, decimal? DoneShare);

/// <summary>
/// The objective roadmap read model (doc 38, Layer 3). It mirrors the SQL views in
/// <c>Data/Sql/roadmap/0001_roadmap_views.sql</c>: a phase gate passes only when it has at least one objective and every
/// objective is done or dropped; an objective with no linked tasks has a null done share. Pure: no I/O, no database, no clock.
/// Task states arrive as the plain strings stored in SQL ("backlog", "ready", "in_progress", "review", "done"). A string
/// outside that set still counts toward <see cref="RoadmapProgress.LinkedTasks"/> but has no bucket of its own.
/// </summary>
public static class RoadmapReadModel
{
    /// <summary>
    /// Builds the view. <paramref name="linkedTaskStatesByObjectivePath"/> maps an objective's Path to the state of each
    /// linked task, one entry per link; an objective missing from it has zero links. Documents that are neither objectives
    /// nor phases are rejected.
    /// </summary>
    public static RoadmapView Build(
        IEnumerable<RoadmapDocumentInput> documents,
        IReadOnlyDictionary<string, IReadOnlyList<string>> linkedTaskStatesByObjectivePath)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(linkedTaskStatesByObjectivePath);

        var phases = new Dictionary<string, RoadmapDocumentInput>(StringComparer.Ordinal);
        var objectives = new List<(RoadmapDocumentInput Input, RoadmapObjective Objective)>();
        foreach (var input in documents)
        {
            ArgumentNullException.ThrowIfNull(input);
            switch (input.Document)
            {
                case RoadmapPhase:
                    if (string.IsNullOrEmpty(input.Slug))
                        throw new ArgumentException($"Phase document '{input.Path}' needs a slug.", nameof(documents));
                    if (!phases.TryAdd(input.Slug, input))
                        throw new ArgumentException($"Duplicate phase slug '{input.Slug}'.", nameof(documents));
                    break;
                case RoadmapObjective objective:
                    if (objectives.Exists(o => o.Input.Path == input.Path))
                        throw new ArgumentException($"Duplicate objective path '{input.Path}'.", nameof(documents));
                    objectives.Add((input, objective));
                    break;
                default:
                    throw new ArgumentException($"Document '{input.Path}' is neither an objective nor a phase.", nameof(documents));
            }
        }

        var objectiveViews = objectives
            .OrderBy(o => o.Input.Path, StringComparer.Ordinal)
            .Select(o => (o.Objective.Phase, View: ToObjectiveView(o.Input, o.Objective, linkedTaskStatesByObjectivePath)))
            .ToList();

        var phaseViews = phases.Values
            .OrderBy(p => p.Slug, StringComparer.Ordinal)
            .Select(phase =>
            {
                var members = objectiveViews.Where(o => o.Phase == phase.Slug).Select(o => o.View).ToImmutableArray();
                var open = members.Count(o => !IsFinished(o.Status));
                return new RoadmapPhaseView(
                    Slug: phase.Slug!,
                    Path: phase.Path,
                    Title: phase.Title,
                    GatePassed: members.Length > 0 && open == 0,
                    ObjectiveCount: members.Length,
                    OpenObjectives: open,
                    Objectives: members);
            })
            .ToList();

        var unphased = objectiveViews
            .Where(o => !phases.ContainsKey(o.Phase))
            .Select(o => o.View)
            .ToList();

        return new RoadmapView(phaseViews, unphased, FindCycle(objectives));
    }

    private static RoadmapObjectiveView ToObjectiveView(
        RoadmapDocumentInput input,
        RoadmapObjective objective,
        IReadOnlyDictionary<string, IReadOnlyList<string>> states)
    {
        var linked = states.TryGetValue(input.Path, out var list) ? list : Array.Empty<string>();
        return new RoadmapObjectiveView(
            Path: input.Path,
            Title: input.Title,
            PhaseSlug: objective.Phase,
            Status: objective.Status,
            Owner: objective.Owner,
            DependsOn: objective.DependsOn,
            Progress: ToProgress(linked));
    }

    private static RoadmapProgress ToProgress(IReadOnlyList<string> states)
    {
        var backlog = 0;
        var ready = 0;
        var inProgress = 0;
        var review = 0;
        var done = 0;
        foreach (var state in states)
        {
            switch (state)
            {
                case "backlog": backlog++; break;
                case "ready": ready++; break;
                case "in_progress": inProgress++; break;
                case "review": review++; break;
                case "done": done++; break;
            }
        }

        var linked = states.Count;
        return new RoadmapProgress(
            LinkedTasks: linked,
            Backlog: backlog,
            Ready: ready,
            InProgress: inProgress,
            Review: review,
            Done: done,
            DoneShare: linked == 0 ? null : (decimal)done / linked);
    }

    private static bool IsFinished(RoadmapStatus status) => status is RoadmapStatus.Done or RoadmapStatus.Dropped;

    /// <summary>
    /// Resolves a bare <c>depends_on</c> slug to an objective path: the path equal to the slug, or the path ending in
    /// <c>/&lt;slug&gt;.md</c>. <paramref name="sortedPaths"/> must be in ordinal order, and the first match wins. Null when no
    /// objective matches.
    /// </summary>
    public static string? ResolveDependency(IReadOnlyList<string> sortedPaths, string slug) =>
        sortedPaths.FirstOrDefault(path => path == slug || path.EndsWith("/" + slug + ".md", StringComparison.Ordinal));

    /// <summary>
    /// Runs <see cref="RoadmapFrontMatter.FindDependencyCycle"/> over the objectives, keyed by Path. Each bare slug in
    /// DependsOn maps to the objective whose Path ends with "/&lt;slug&gt;.md" or equals the slug exactly. When several
    /// objectives match one slug, the first in ordinal Path order wins. Slugs that match no objective add no edge.
    /// </summary>
    private static IReadOnlyList<string> FindCycle(IReadOnlyList<(RoadmapDocumentInput Input, RoadmapObjective Objective)> objectives)
    {
        var paths = objectives.Select(o => o.Input.Path).Order(StringComparer.Ordinal).ToList();
        var edges = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (input, objective) in objectives)
        {
            var targets = objective.DependsOn
                .Select(slug => ResolveDependency(paths, slug))
                .OfType<string>()
                .ToList();
            edges.Add(input.Path, targets);
        }

        return RoadmapFrontMatter.FindDependencyCycle(edges);
    }
}
