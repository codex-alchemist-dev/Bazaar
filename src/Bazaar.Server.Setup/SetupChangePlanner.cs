using System.Text.Json;

namespace Bazaar.Server.Setup;

/// <summary>The dry-run result: the plan hash and the change list. Nothing is written.</summary>
/// <param name="PlanHash">The hash that apply must be given (doc 45 section 1.2).</param>
/// <param name="Changes">Every create, update and remove, sorted by path. Unchanged leaves are not listed.</param>
public sealed record SetupChangeSet(string PlanHash, IReadOnlyList<SetupChange> Changes);

/// <summary>
/// Computes the change list between a normalized plan and a current-state snapshot. It is a pure function: it reads the plan
/// and the snapshot and returns the hash and the changes. Apply is not part of this slice.
/// </summary>
public static class SetupChangePlanner
{
    public static SetupChangeSet Compute(SetupPlan plan, SetupStateSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(current);

        var planned = SetupPlanCanonicalizer.Leaves(SetupPlanCanonicalizer.ToNode(plan));
        var changes = new List<SetupChange>();

        foreach (var (path, plannedText) in planned)
        {
            if (!current.LeafValues.TryGetValue(path, out var currentText))
            {
                changes.Add(new SetupChange(path, SetupChangeKind.Create, null, plannedText));
            }
            else if (!string.Equals(currentText, plannedText, StringComparison.Ordinal))
            {
                changes.Add(new SetupChange(path, SetupChangeKind.Update, currentText, plannedText));
            }
        }

        foreach (var (path, currentText) in current.LeafValues)
        {
            if (!planned.ContainsKey(path))
            {
                changes.Add(new SetupChange(path, SetupChangeKind.Remove, currentText, null));
            }
        }

        changes.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new SetupChangeSet(SetupPlanCanonicalizer.Hash(plan), changes);
    }
}
