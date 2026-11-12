using System.Collections.Immutable;
using Bazaar.Contracts.Roadmap;
using Bazaar.RyuseiFuda.Roadmap;

namespace Bazaar.Server.Tests.Roadmap;

/// <summary>The wire snapshot mirrors the read model: phases, gates, statuses, progress, errors. Pure: no database.</summary>
public sealed class RoadmapSnapshotTests
{
    [Fact]
    public void Snapshot_maps_gate_status_progress_and_passes_errors_through()
    {
        var documents = new[]
        {
            new RoadmapDocumentInput("roadmap/phases/one.md", "Phase one", "one", new RoadmapPhase(RoadmapStatus.Active)),
            new RoadmapDocumentInput("roadmap/objectives/a.md", "A", null,
                new RoadmapObjective("one", RoadmapStatus.Done, "agent:x", ["b"])),
            new RoadmapDocumentInput("roadmap/objectives/b.md", "B", null,
                new RoadmapObjective("one", RoadmapStatus.Active, null, ImmutableArray<string>.Empty)),
        };
        var states = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["roadmap/objectives/a.md"] = ["done", "in_progress"],
        };
        var view = RoadmapReadModel.Build(documents, states);
        var errors = new List<RoadmapDocumentErrorDto> { new("roadmap/objectives/bad.md", "status", "status is required.") };

        var snapshot = RoadmapSnapshotMapper.ToSnapshot(Guid.Empty, view, errors);

        var phase = Assert.Single(snapshot.Phases);
        Assert.Equal("one", phase.Slug);
        Assert.False(phase.GatePassed);
        Assert.Equal(2, phase.ObjectiveCount);
        Assert.Equal(1, phase.OpenObjectives);

        var a = phase.Objectives.Single(o => o.Path == "roadmap/objectives/a.md");
        Assert.Equal("done", a.Status);
        Assert.Equal("agent:x", a.Owner);
        Assert.Equal(["b"], a.DependsOn);
        Assert.Equal(2, a.Progress.LinkedTasks);
        Assert.Equal(1, a.Progress.Done);
        Assert.Equal(1, a.Progress.InProgress);
        Assert.Equal(0.5m, a.Progress.DoneShare);

        var b = phase.Objectives.Single(o => o.Path == "roadmap/objectives/b.md");
        Assert.Equal("active", b.Status);
        Assert.Null(b.Progress.DoneShare);

        Assert.Equal("status", Assert.Single(snapshot.Errors).Field);
        Assert.Empty(snapshot.DependencyCycle);
    }

    [Fact]
    public void A_phase_with_no_objectives_does_not_pass_its_gate()
    {
        var view = RoadmapReadModel.Build(
            [new RoadmapDocumentInput("roadmap/phases/empty.md", "Empty", "empty", new RoadmapPhase(RoadmapStatus.Planned))],
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));

        var phase = Assert.Single(RoadmapSnapshotMapper.ToSnapshot(Guid.Empty, view, []).Phases);

        Assert.False(phase.GatePassed);
        Assert.Equal(0, phase.ObjectiveCount);
    }
}
