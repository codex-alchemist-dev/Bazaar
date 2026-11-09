using System.Collections.Immutable;
using Bazaar.Server.Roadmap;

namespace Bazaar.Server.Tests.Roadmap;

/// <summary>The objective roadmap read model (doc 38, Layer 3): progress, phase gates, ordering and cycles. Pure: no database.</summary>
public sealed class RoadmapReadModelTests
{
    private static RoadmapDocumentInput Objective(string path, string phase, RoadmapStatus status, string? owner = null, params string[] dependsOn) =>
        new(path, $"Title of {path}", null, new RoadmapObjective(phase, status, owner, [.. dependsOn]));

    private static RoadmapDocumentInput Phase(string slug, RoadmapStatus status = RoadmapStatus.Active) =>
        new($"roadmap/phases/{slug}.md", $"Phase {slug}", slug, new RoadmapPhase(status));

    private static Dictionary<string, IReadOnlyList<string>> Links(params (string Path, string[] States)[] entries) =>
        entries.ToDictionary(e => e.Path, e => (IReadOnlyList<string>)e.States, StringComparer.Ordinal);

    private static Dictionary<string, IReadOnlyList<string>> NoLinks() =>
        new(StringComparer.Ordinal);

    [Fact]
    public void Progress_counts_each_state_and_derives_done_share()
    {
        var documents = new[] { Phase("one"), Objective("roadmap/objectives/a.md", "one", RoadmapStatus.Active) };
        var view = RoadmapReadModel.Build(documents, Links(
            ("roadmap/objectives/a.md", ["done", "done", "in_progress", "review", "backlog", "ready"])));

        var progress = Assert.Single(Assert.Single(view.Phases).Objectives).Progress;
        Assert.Equal(6, progress.LinkedTasks);
        Assert.Equal(2, progress.Done);
        Assert.Equal(1, progress.InProgress);
        Assert.Equal(1, progress.Review);
        Assert.Equal(1, progress.Backlog);
        Assert.Equal(1, progress.Ready);
        Assert.Equal((decimal)2 / 6, progress.DoneShare);
    }

    [Fact]
    public void Objective_with_no_links_has_zero_counts_and_null_done_share()
    {
        var documents = new[] { Phase("one"), Objective("roadmap/objectives/a.md", "one", RoadmapStatus.Planned) };

        var view = RoadmapReadModel.Build(documents, NoLinks());

        var progress = Assert.Single(Assert.Single(view.Phases).Objectives).Progress;
        Assert.Equal(0, progress.LinkedTasks);
        Assert.Equal(0, progress.Done);
        Assert.Null(progress.DoneShare);
    }

    [Fact]
    public void Unknown_state_counts_toward_linked_tasks_but_no_bucket()
    {
        var documents = new[] { Phase("one"), Objective("roadmap/objectives/a.md", "one", RoadmapStatus.Active) };
        var view = RoadmapReadModel.Build(documents, Links(("roadmap/objectives/a.md", ["done", "archived"])));

        var progress = Assert.Single(Assert.Single(view.Phases).Objectives).Progress;
        Assert.Equal(2, progress.LinkedTasks);
        Assert.Equal(1, progress.Done);
        Assert.Equal(0, progress.Backlog + progress.Ready + progress.InProgress + progress.Review);
        Assert.Equal((decimal)1 / 2, progress.DoneShare);
    }

    [Fact]
    public void Gate_passes_when_every_objective_is_done_or_dropped()
    {
        var documents = new[]
        {
            Phase("one"),
            Objective("roadmap/objectives/a.md", "one", RoadmapStatus.Done),
            Objective("roadmap/objectives/b.md", "one", RoadmapStatus.Dropped),
        };

        var phase = Assert.Single(RoadmapReadModel.Build(documents, NoLinks()).Phases);

        Assert.True(phase.GatePassed);
        Assert.Equal(2, phase.ObjectiveCount);
        Assert.Equal(0, phase.OpenObjectives);
    }

    [Fact]
    public void Gate_fails_for_an_empty_phase()
    {
        var view = RoadmapReadModel.Build([Phase("empty")], NoLinks());

        var phase = Assert.Single(view.Phases);
        Assert.False(phase.GatePassed);
        Assert.Equal(0, phase.ObjectiveCount);
        Assert.Equal(0, phase.OpenObjectives);
    }

    [Fact]
    public void Gate_fails_with_one_active_objective()
    {
        var documents = new[]
        {
            Phase("one"),
            Objective("roadmap/objectives/a.md", "one", RoadmapStatus.Done),
            Objective("roadmap/objectives/b.md", "one", RoadmapStatus.Active),
        };

        var phase = Assert.Single(RoadmapReadModel.Build(documents, NoLinks()).Phases);

        Assert.False(phase.GatePassed);
        Assert.Equal(2, phase.ObjectiveCount);
        Assert.Equal(1, phase.OpenObjectives);
    }

    [Fact]
    public void Objective_with_no_phase_document_is_unphased_and_gates_nothing()
    {
        var documents = new[]
        {
            Phase("one"),
            Objective("roadmap/objectives/a.md", "one", RoadmapStatus.Done),
            Objective("roadmap/objectives/orphan.md", "missing", RoadmapStatus.Active, "codex-alchemist"),
        };

        var view = RoadmapReadModel.Build(documents, NoLinks());

        var phase = Assert.Single(view.Phases);
        Assert.True(phase.GatePassed);
        Assert.Equal(1, phase.ObjectiveCount);
        var orphan = Assert.Single(view.UnphasedObjectives);
        Assert.Equal("roadmap/objectives/orphan.md", orphan.Path);
        Assert.Equal("missing", orphan.PhaseSlug);
        Assert.Equal("codex-alchemist", orphan.Owner);
    }

    [Fact]
    public void Phases_order_by_slug_and_objectives_by_path()
    {
        var documents = new[]
        {
            Phase("beta"),
            Phase("alpha"),
            Objective("roadmap/objectives/z.md", "alpha", RoadmapStatus.Planned),
            Objective("roadmap/objectives/m.md", "alpha", RoadmapStatus.Planned),
            Objective("roadmap/objectives/b.md", "beta", RoadmapStatus.Planned),
        };

        var view = RoadmapReadModel.Build(documents, NoLinks());

        Assert.Equal(["alpha", "beta"], view.Phases.Select(p => p.Slug));
        Assert.Equal(
            ["roadmap/objectives/m.md", "roadmap/objectives/z.md"],
            view.Phases[0].Objectives.Select(o => o.Path));
        Assert.Equal(["roadmap/objectives/b.md"], view.Phases[1].Objectives.Select(o => o.Path));
    }

    [Fact]
    public void Dependency_cycle_is_reported_by_path()
    {
        var documents = new[]
        {
            Phase("one"),
            Objective("roadmap/objectives/a.md", "one", RoadmapStatus.Planned, null, "b"),
            Objective("roadmap/objectives/b.md", "one", RoadmapStatus.Planned, null, "a"),
        };

        var view = RoadmapReadModel.Build(documents, NoLinks());

        Assert.Equal(2, view.DependencyCycle.Count);
        Assert.Contains("roadmap/objectives/a.md", view.DependencyCycle);
        Assert.Contains("roadmap/objectives/b.md", view.DependencyCycle);
    }

    [Fact]
    public void Acyclic_graph_reports_no_cycle()
    {
        var documents = new[]
        {
            Phase("one"),
            Objective("roadmap/objectives/a.md", "one", RoadmapStatus.Planned, null, "b"),
            Objective("roadmap/objectives/b.md", "one", RoadmapStatus.Planned),
        };

        var view = RoadmapReadModel.Build(documents, NoLinks());

        Assert.Empty(view.DependencyCycle);
    }

    [Fact]
    public void Dependency_on_an_unknown_slug_adds_no_edge()
    {
        var documents = new[]
        {
            Phase("one"),
            Objective("roadmap/objectives/a.md", "one", RoadmapStatus.Planned, null, "nowhere"),
        };

        var view = RoadmapReadModel.Build(documents, NoLinks());

        Assert.Empty(view.DependencyCycle);
        Assert.Equal(["nowhere"], Assert.Single(view.Phases[0].Objectives).DependsOn);
    }

    [Fact]
    public void Phase_without_a_slug_is_rejected()
    {
        var phase = new RoadmapDocumentInput("roadmap/phases/one.md", "Phase one", null, new RoadmapPhase(RoadmapStatus.Active));

        Assert.Throws<ArgumentException>(() => RoadmapReadModel.Build([phase], NoLinks()));
    }
}
