namespace Bazaar.Contracts.Roadmap;

/// <summary>
/// The objective roadmap of one Vault as the wire sends it (doc 38, Layer 3). Derived on the server from Vault documents and
/// task links; nothing here is stored. Ids are paths and slugs, so the same shape serves the roadmap pane and the Constellation.
/// </summary>
/// <param name="VaultId">The Vault the roadmap was read from.</param>
/// <param name="Phases">Phases ordered by slug.</param>
/// <param name="UnphasedObjectives">Objectives whose phase slug names no phase document, ordered by path.</param>
/// <param name="DependencyCycle">One dependency cycle among objectives, in cycle order; empty when the graph is acyclic.</param>
/// <param name="Errors">Documents that were left out because their front matter does not meet the contract.</param>
public sealed record RoadmapBoardSnapshot(
    Guid VaultId,
    IReadOnlyList<RoadmapPhaseDto> Phases,
    IReadOnlyList<RoadmapObjectiveDto> UnphasedObjectives,
    IReadOnlyList<string> DependencyCycle,
    IReadOnlyList<RoadmapDocumentErrorDto> Errors);

/// <summary>One phase with its gate. <see cref="GatePassed"/> is false for an empty phase.</summary>
public sealed record RoadmapPhaseDto(
    string Slug,
    string Path,
    string Title,
    bool GatePassed,
    int ObjectiveCount,
    int OpenObjectives,
    IReadOnlyList<RoadmapObjectiveDto> Objectives);

/// <summary>One objective. <see cref="Status"/> is "planned", "active", "done" or "dropped".</summary>
/// <param name="DependsOn">Bare slugs of the objectives this one depends on, as written.</param>
public sealed record RoadmapObjectiveDto(
    string Path,
    string Title,
    string PhaseSlug,
    string Status,
    string? Owner,
    IReadOnlyList<string> DependsOn,
    RoadmapProgressDto Progress);

/// <summary>Task progress for one objective. <see cref="DoneShare"/> is null when no task is linked.</summary>
public sealed record RoadmapProgressDto(
    int LinkedTasks,
    int Backlog,
    int Ready,
    int InProgress,
    int Review,
    int Done,
    decimal? DoneShare);

/// <summary>A Vault document that was left out of the roadmap, with the front-matter key at fault.</summary>
public sealed record RoadmapDocumentErrorDto(string Path, string Field, string Message);
