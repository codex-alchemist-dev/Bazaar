using Bazaar.Server.Components.Backup.Recovery;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Audit.Checkpoints;

namespace Bazaar.Server.Components.Backup.Restore;

/// <summary>
/// Says whether the install being restored onto already holds data (doc 48 section 6: restore never silently overwrites
/// live data). Implementations look at the database, the Soul folders and the Vault. Contract only.
/// </summary>
public interface IRestoreTargetProbe
{
    /// <summary>True when any of the database, the Soul folders or the Vault holds live data.</summary>
    Task<bool> IsPopulatedAsync(CancellationToken ct);
}

/// <summary>
/// Moves existing live data aside before an owner-confirmed overwrite. It must never delete: the preserved copy is the
/// way back if the owner confirmed by mistake. Without an implementation, an overwrite is refused outright.
/// </summary>
public interface ILiveDataPreserver
{
    /// <summary>Preserves everything the restore would replace, under a location tied to <paramref name="restoreId"/>.</summary>
    Task PreserveAsync(string restoreId, CancellationToken ct);
}

/// <summary>
/// Reads the backup sets named in the bundle so the wizard can compare each against its recorded checksum before any
/// restore step touches the install.
/// </summary>
public interface IRestoreBackupSource
{
    /// <summary>Returns the hex SHA-256 and the size in bytes of the named backup set, or null when it cannot be found.</summary>
    Task<(string Sha256Hex, long SizeBytes)?> DescribeAsync(string name, CancellationToken ct);
}

/// <summary>Restores the key-encryption-key export from the bundle (doc 48 section 6). Contract only.</summary>
public interface IRestoreKeyRestorer
{
    /// <summary>
    /// Installs the sealed KEK export. The buffer is secret and is zeroed by the wizard after this returns, so an
    /// implementation must not keep it.
    /// </summary>
    Task RestoreKeysAsync(ReadOnlyMemory<byte> kekExport, CancellationToken ct);
}

/// <summary>Restores the database from the backup sets, to a point in time when one is given. Contract only.</summary>
public interface IRestoreDatabaseRestorer
{
    /// <summary>Restores the named sets. A null <paramref name="pointInTime"/> restores to the end of the last set.</summary>
    Task RestoreDatabaseAsync(IReadOnlyList<RecoveryBackupSetEntry> sets, DateTimeOffset? pointInTime, CancellationToken ct);
}

/// <summary>Restores the Soul folders and the Vault repository from the backup sets. Contract only.</summary>
public interface IRestoreFilesRestorer
{
    /// <summary>Restores every agent Soul folder.</summary>
    Task RestoreSoulFoldersAsync(IReadOnlyList<RecoveryBackupSetEntry> sets, CancellationToken ct);

    /// <summary>Restores the Vault (Markdown files in a git repository).</summary>
    Task RestoreVaultAsync(IReadOnlyList<RecoveryBackupSetEntry> sets, CancellationToken ct);
}

/// <summary>Rebuilds the derived indexes (search, Vault index, knowledge graph) from the restored sources. Contract only.</summary>
public interface IRestoreIndexRebuilder
{
    /// <summary>Rebuilds every index from the restored database and Vault.</summary>
    Task RebuildAsync(CancellationToken ct);
}

/// <summary>What the wizard needs to verify the audit chain after a restore.</summary>
/// <param name="Anchor">The last anchored checkpoint, as held by the external anchor.</param>
/// <param name="Signature">The signature over <see cref="AuditCheckpoint.SignedBytes"/>.</param>
/// <param name="RowsAfterAnchor">The audit rows after the anchor, as found in the restored database.</param>
public sealed record RestoreAuditEvidence(AuditCheckpoint Anchor, byte[] Signature, IReadOnlyList<AuditRecord> RowsAfterAnchor);

/// <summary>
/// Supplies the last anchored checkpoint and the restored rows after it. The anchor comes from an external target
/// (doc 48 section 5.3), never from the restored database alone, so a rewritten database cannot vouch for itself.
/// </summary>
public interface IRestoreAuditEvidenceSource
{
    /// <summary>
    /// Loads the evidence for the checkpoint whose chain head is <paramref name="bundleHead"/>. Returns null when no
    /// anchored checkpoint with that head can be found, which the wizard treats as a failed verification.
    /// </summary>
    Task<RestoreAuditEvidence?> LoadAsync(byte[] bundleHead, CancellationToken ct);
}

/// <summary>Appends restore events to the audit log. Payloads carry the restore id and never secret material.</summary>
public interface IRestoreAuditSink
{
    /// <summary>Appends one event. Must complete only when the event is durable.</summary>
    Task AppendAsync(RecoveryAuditEvent auditEvent, CancellationToken ct);
}

/// <summary>
/// The write switch of the install. The wizard turns writes off before any restore step and turns them on only after the
/// owner confirms (doc 48 section 6).
/// </summary>
public interface IRestoreWriteGate
{
    /// <summary>Puts the install in read-only mode. Must be safe to call when already read-only.</summary>
    Task EnterReadOnlyAsync(string restoreId, CancellationToken ct);

    /// <summary>Enables writes. Called only after the owner's confirmation.</summary>
    Task EnableWritesAsync(string restoreId, CancellationToken ct);
}
