using Bazaar.Server.CodingBench.Abstractions;

namespace Bazaar.Server.CodingBench;

/// <summary>
/// The task mix doc 17 requires for the held-out set: minimum counts per kind, traps included.
/// It describes the mix only; the task content comes from Bazaar history later.
/// </summary>
public static class CodingBenchTaskSet
{
    private static readonly (CodingTaskKind Kind, int MinimumCount)[] _mix =
    [
        (CodingTaskKind.BugFix, 11),
        (CodingTaskKind.FeatureWithTest, 10),
        (CodingTaskKind.Refactor, 6),
        (CodingTaskKind.AvaloniaHeadlessUi, 4),
        (CodingTaskKind.GodotCheck, 3),
        (CodingTaskKind.LuauLintFix, 3),
        (CodingTaskKind.TrapWrongTest, 1),
        (CodingTaskKind.TrapImpossible, 1),
        (CodingTaskKind.TrapInjectedIssue, 1),
    ];

    /// <summary>The required mix: each kind with its minimum task count.</summary>
    public static IReadOnlyList<(CodingTaskKind Kind, int MinimumCount)> Describe() => _mix;

    /// <summary>Human-readable shortfalls against the mix. Empty when the task list satisfies it.</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<CodingTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        var counts = tasks
            .GroupBy(task => task.Kind)
            .ToDictionary(group => group.Key, group => group.Count());

        var shortfalls = new List<string>();
        foreach (var (kind, minimum) in _mix)
        {
            var have = counts.GetValueOrDefault(kind);
            if (have < minimum)
            {
                shortfalls.Add($"{kind}: {have} of {minimum} required");
            }
        }

        return shortfalls;
    }
}
