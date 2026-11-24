namespace Bazaar.Server.Components.Backup.Jobs;

/// <summary>Configuration for the opt-in recurring PostgreSQL base-backup job.</summary>
public sealed class BaseBackupJobOptions
{
    public const string SectionName = "Bazaar:Backup:Base";

    /// <summary>Backups are opt-in until an operator supplies the storage and PostgreSQL binary paths.</summary>
    public bool Enabled { get; set; }

    public string SetsRoot { get; set; } = string.Empty;

    public string PgBinDirectory { get; set; } = string.Empty;

    public string? ArchiveDirectory { get; set; }

    public int BaseSetsKept { get; set; } = 2;

    /// <summary>Upload attempts per backup run when an offsite target is configured (doc 33, "Offsite composition [PROPOSED]").</summary>
    public int OffsiteAttempts { get; set; } = 3;

    /// <summary>Wait between offsite upload attempts.</summary>
    public TimeSpan OffsiteRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(24);
}
