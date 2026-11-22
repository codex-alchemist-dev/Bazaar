using Bazaar.Server.CodingBench.Abstractions;

namespace Bazaar.Server.CodingBench.Tests;

public sealed class CodingBenchTaskSetTests
{
    private static List<CodingTask> FullMix()
    {
        var tasks = new List<CodingTask>();
        foreach (var (kind, minimum) in CodingBenchTaskSet.Describe())
        {
            for (var index = 0; index < minimum; index++)
            {
                tasks.Add(new CodingTask($"{kind}-{index}", kind, kind.ToString(), "p", "c", "v", ExpectedOutcome.Pass));
            }
        }

        return tasks;
    }

    [Fact]
    public void Describe_lists_every_kind_in_the_doc_mix_with_the_traps()
    {
        var mix = CodingBenchTaskSet.Describe();

        Assert.Equal(9, mix.Count);
        Assert.Equal(11, mix.Single(m => m.Kind == CodingTaskKind.BugFix).MinimumCount);
        Assert.Equal(10, mix.Single(m => m.Kind == CodingTaskKind.FeatureWithTest).MinimumCount);
        Assert.Equal(6, mix.Single(m => m.Kind == CodingTaskKind.Refactor).MinimumCount);
        Assert.Equal(4, mix.Single(m => m.Kind == CodingTaskKind.AvaloniaHeadlessUi).MinimumCount);
        Assert.Equal(3, mix.Single(m => m.Kind == CodingTaskKind.GodotCheck).MinimumCount);
        Assert.Equal(3, mix.Single(m => m.Kind == CodingTaskKind.LuauLintFix).MinimumCount);
        Assert.Equal(1, mix.Single(m => m.Kind == CodingTaskKind.TrapWrongTest).MinimumCount);
        Assert.Equal(1, mix.Single(m => m.Kind == CodingTaskKind.TrapImpossible).MinimumCount);
        Assert.Equal(1, mix.Single(m => m.Kind == CodingTaskKind.TrapInjectedIssue).MinimumCount);
    }

    [Fact]
    public void A_task_list_that_meets_the_mix_has_no_shortfalls()
    {
        Assert.Empty(CodingBenchTaskSet.Validate(FullMix()));
    }

    [Fact]
    public void A_missing_kind_is_reported_as_a_shortfall()
    {
        var tasks = FullMix().Where(t => t.Kind != CodingTaskKind.TrapImpossible).ToList();

        var shortfalls = CodingBenchTaskSet.Validate(tasks);

        var shortfall = Assert.Single(shortfalls);
        Assert.Contains("TrapImpossible", shortfall, StringComparison.Ordinal);
        Assert.Contains("0 of 1", shortfall, StringComparison.Ordinal);
    }

    [Fact]
    public void A_partial_kind_reports_how_many_are_missing()
    {
        var tasks = FullMix();
        tasks.RemoveAll(t => t.Kind == CodingTaskKind.BugFix && t.Id != "BugFix-0");

        var shortfall = Assert.Single(CodingBenchTaskSet.Validate(tasks));

        Assert.Contains("BugFix", shortfall, StringComparison.Ordinal);
        Assert.Contains("1 of 11", shortfall, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_list_reports_every_kind()
    {
        var shortfalls = CodingBenchTaskSet.Validate([]);

        Assert.Equal(CodingBenchTaskSet.Describe().Count, shortfalls.Count);
    }
}
