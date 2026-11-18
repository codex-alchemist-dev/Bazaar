using Bazaar.Contracts.Backup;

namespace Bazaar.Server.Backup.Retention;

/// <summary>
/// What to keep and delete. <see cref="WalKeepFrom"/> is the oldest kept base set's start WAL segment; WAL older than it
/// is not needed by any kept set (doc 33, <c>backup.base_sets_kept</c>).
/// </summary>
public sealed record RetentionPlan(IReadOnlyList<BackupSet> Keep, IReadOnlyList<BackupSet> Delete, string WalKeepFrom);

/// <summary>Pure retention rules. Never deletes the newest <c>keep</c> sets, and never plans a delete before a newer set exists.</summary>
public static class RetentionPlanner
{
    public static RetentionPlan Plan(IReadOnlyList<BackupSet> sets, int baseSetsKept)
    {
        if (baseSetsKept < 1)
            throw new ArgumentOutOfRangeException(nameof(baseSetsKept), "At least one base set must be kept.");
        if (sets.Count == 0)
            throw new InvalidOperationException("Retention needs at least one base set.");

        var newestFirst = sets.OrderByDescending(s => s.TakenAt).ThenByDescending(s => s.SetId, StringComparer.Ordinal).ToList();
        var keep = newestFirst.Take(baseSetsKept).ToList();
        var delete = newestFirst.Skip(baseSetsKept).ToList();
        var walKeepFrom = keep[^1].StartWal;
        return new RetentionPlan(keep, delete, walKeepFrom);
    }
}
