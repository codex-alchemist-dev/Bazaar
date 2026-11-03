namespace Bazaar.Contracts.Backup;

/// <summary>What a backup run produced (doc 33, Settings and Work packages 1, 3, 4, 8).</summary>
public enum BackupKind
{
    /// <summary>A physical base backup of the PostgreSQL data directory.</summary>
    BaseBackup,

    /// <summary>Retention cleanup of archived WAL and old base sets.</summary>
    Retention,

    /// <summary>A copy of local backup sets to an offsite target.</summary>
    Offsite,

    /// <summary>A restore into an empty target (doc 33 restore rules).</summary>
    Restore,
}

/// <summary>Outcome state of one run. Runs are recorded before they start and finished after they end.</summary>
public enum BackupState
{
    Running,
    Succeeded,
    Failed,
}

/// <summary>
/// One backup or restore run, as shown on the backup status card (doc 33 WP12). Byte counts and digests are
/// measured, never estimated. <see cref="Detail"/> never holds secret values.
/// </summary>
public sealed record BackupRun(
    Guid RunId,
    BackupKind Kind,
    BackupState State,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    long Bytes,
    string? SetId,
    string? Detail);

/// <summary>A base backup set on local disk. <see cref="StartWal"/> is the WAL segment name from <c>backup_label</c>.</summary>
public sealed record BackupSet(
    string SetId,
    string Path,
    DateTimeOffset TakenAt,
    long Bytes,
    string StartWal);

/// <summary>A snapshot held by an offsite target.</summary>
public sealed record OffsiteSnapshot(string SnapshotId, DateTimeOffset TakenAt, IReadOnlyList<string> Tags);

/// <summary>
/// Offsite store (doc 33 Operator hooks, <c>backup.offsite_target</c>). Implementations send only bytes the caller
/// hands over; nothing leaves the install unless an operator configured a target.
/// </summary>
public interface IBackupTarget
{
    /// <summary>Stable name for logs and the status card, for example <c>restic-s3</c> or <c>fake</c>.</summary>
    string Name { get; }

    /// <summary>Uploads a local directory as one snapshot and returns its id.</summary>
    Task<OffsiteSnapshot> PutDirectoryAsync(string localDirectory, IReadOnlyList<string> tags, CancellationToken ct);

    /// <summary>Lists snapshots, newest first.</summary>
    Task<IReadOnlyList<OffsiteSnapshot>> ListAsync(CancellationToken ct);
}

/// <summary>
/// Local base-backup and WAL engine (doc 33 PostgreSQL paths). Windows uses <c>pg_basebackup</c> and the Bazaar archiver;
/// Linux uses pgBackRest (doc 33, decision in the same change).
/// </summary>
public interface IBackupEngine
{
    /// <summary>Stable name, for example <c>pg_basebackup</c> or <c>pgbackrest</c>.</summary>
    string Name { get; }

    /// <summary>Takes a base backup into <paramref name="destinationDirectory"/>, which must not exist yet.</summary>
    Task<BackupSet> TakeBaseBackupAsync(string destinationDirectory, CancellationToken ct);
}
