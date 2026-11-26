namespace Bazaar.Server.BackupPolicy;

/// <summary>
/// The names and shipped defaults of the operator's backup policy settings (doc 33 settings table; doc 45 step 8). Pure constants,
/// so the setup projects can name the keys without referencing the module that registers them.
/// </summary>
public static class BackupPolicySettingNames
{
    /// <summary>
    /// The operator's chosen backup location, as <c>scheme:rest</c>. Empty means none is configured. Recording it does not start
    /// a backup: the Backup module reads it when its scheduler is built (doc 33).
    /// </summary>
    public const string Target = "backup.target";

    /// <summary>Days between restore drills (doc 33 settings table: <c>backup.drill_interval_days</c>).</summary>
    public const string DrillIntervalDays = "backup.drill_interval_days";

    /// <summary>
    /// True when the operator finished setup with no target and acknowledged it (doc 45, Open questions item 5). Kodo keeps flagging
    /// a missing target while this is the only thing standing in for one (doc 33, doc 46).
    /// </summary>
    public const string NoTargetAcknowledged = "backup.no_target_acknowledged";

    /// <summary>The doc 33 shipped default for <see cref="DrillIntervalDays"/>.</summary>
    public const int DefaultDrillIntervalDays = 90;

    /// <summary>The longest interval accepted, matching the setup plan.</summary>
    public const int MaxDrillIntervalDays = 365;

    /// <summary>The scheme of a target on the install's own disks or a mounted share. It is the only scheme that needs no owner approval.</summary>
    public const string LocalScheme = "file";

    /// <summary>Every scheme a target may use: a local path, restic to an S3-compatible bucket, restic to Backblaze B2, or a custom <c>IBackupTarget</c> (doc 33, offsite store).</summary>
    public static readonly IReadOnlyList<string> Schemes = [LocalScheme, "s3", "b2", "custom"];
}
