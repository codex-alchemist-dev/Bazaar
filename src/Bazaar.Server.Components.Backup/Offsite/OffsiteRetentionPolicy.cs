namespace Bazaar.Server.Components.Backup.Offsite;

/// <summary>
/// Offsite retention limits (doc 33, Conflict 8). Daily and weekly snapshots must stay inside the erasure window, so the
/// newest offsite copy of erased data expires with its backup. The window is a placeholder until counsel confirms it.
/// </summary>
public static class OffsiteRetentionPolicy
{
    /// <summary>Doc 04 placeholder for <c>erasure.backup_window_days</c>.</summary>
    public const int DefaultErasureWindowDays = 35;

    public const int ShippedKeepDaily = 14;
    public const int ShippedKeepWeekly = 4;

    /// <summary>Throws when the settings would keep a snapshot older than the erasure window.</summary>
    public static void Validate(int keepDaily, int keepWeekly, int erasureWindowDays = DefaultErasureWindowDays)
    {
        if (keepDaily < 0 || keepWeekly < 0)
            throw new ArgumentOutOfRangeException(nameof(keepDaily), "Retention counts cannot be negative.");
        if (keepDaily > erasureWindowDays)
            throw new ArgumentOutOfRangeException(nameof(keepDaily), $"Keeping {keepDaily} daily snapshots exceeds the {erasureWindowDays}-day erasure window.");
        if (keepWeekly * 7 > erasureWindowDays)
            throw new ArgumentOutOfRangeException(nameof(keepWeekly), $"Keeping {keepWeekly} weekly snapshots exceeds the {erasureWindowDays}-day erasure window.");
    }
}
