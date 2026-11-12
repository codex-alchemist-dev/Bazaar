using Bazaar.Contracts.Roadmap;

namespace Bazaar.RyuseiFuda.Roadmap;

/// <summary>Maps the server's read model to the wire snapshot. Pure.</summary>
public static class RoadmapSnapshotMapper
{
    public static RoadmapBoardSnapshot ToSnapshot(Guid vaultId, RoadmapView view, IReadOnlyList<RoadmapDocumentErrorDto> errors)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(errors);

        return new RoadmapBoardSnapshot(
            vaultId,
            view.Phases.Select(ToPhase).ToList(),
            view.UnphasedObjectives.Select(ToObjective).ToList(),
            view.DependencyCycle,
            errors);
    }

    private static RoadmapPhaseDto ToPhase(RoadmapPhaseView phase) =>
        new(phase.Slug, phase.Path, phase.Title, phase.GatePassed, phase.ObjectiveCount, phase.OpenObjectives,
            phase.Objectives.Select(ToObjective).ToList());

    private static RoadmapObjectiveDto ToObjective(RoadmapObjectiveView objective) =>
        new(objective.Path, objective.Title, objective.PhaseSlug, objective.Status.ToString().ToLowerInvariant(), objective.Owner,
            objective.DependsOn, ToProgress(objective.Progress));

    private static RoadmapProgressDto ToProgress(RoadmapProgress progress) =>
        new(progress.LinkedTasks, progress.Backlog, progress.Ready, progress.InProgress, progress.Review, progress.Done, progress.DoneShare);
}
