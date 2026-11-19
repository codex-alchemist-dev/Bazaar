namespace Bazaar.Components.ManagedPostgres;

/// <summary>
/// Scheduled logical dumps of the managed cluster (doc 33: nightly <c>pg_dump -Fc</c>, a logical copy only). Bound from
/// <c>Bazaar:Database:Backup</c>. Only runs in Managed mode, because an External server is the operator's to back up.
/// </summary>
public sealed class ManagedPostgresBackupOptions
{
    /// <summary>Safe default: on. Set to false when the operator backs the cluster up another way.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Where dumps go. Empty means <c>DataRoot/backups</c>.</summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>Time between dumps. The first dump runs after one interval, once the cluster is up.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Newest dumps kept for this major. Older dumps and their checksums are deleted after each run.</summary>
    public int KeepDumps { get; set; } = 7;
}
