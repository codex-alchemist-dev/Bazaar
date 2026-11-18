namespace Bazaar.OfflineSync;

/// <summary>The data classes that can be written offline (doc 49 section 4). Settings and policies are deliberately absent.</summary>
public enum OfflineDataClass
{
    /// <summary>A new GC message (append-only).</summary>
    GcMessage = 1,
    /// <summary>A GC message edit (new version row).</summary>
    GcMessageEdit = 2,
    /// <summary>A Vault commit push intent (git rule lives in the Vault module).</summary>
    VaultCommit = 3,
    /// <summary>A Kanban task field change (field-level merge).</summary>
    KanbanField = 4,
    /// <summary>A Kanban dependency edge add or remove.</summary>
    KanbanEdge = 5,
    /// <summary>A Constellation node position (last-writer-wins).</summary>
    ConstellationPosition = 6,
    /// <summary>A user preference key (last-writer-wins).</summary>
    Preference = 7,
    /// <summary>A content-addressed attachment.</summary>
    Attachment = 8,
    /// <summary>A client audit action (server-ordered).</summary>
    AuditAction = 9,
}

/// <summary>One queued offline write. <see cref="WriteId"/> is the idempotency key.</summary>
/// <param name="WriteId">Client-generated identifier. A replay with the same id never creates a second effect.</param>
/// <param name="DeviceId">The writing device.</param>
/// <param name="DeviceSequence">Gapless per-device sequence, starting at 1.</param>
/// <param name="DataClass">Which merge rule applies.</param>
/// <param name="Target">The object key (GC id, task id, vault path, preference key, node id, content hash).</param>
/// <param name="BaseVersion">The version the write was made against (per field for Kanban); null for append-only classes.</param>
/// <param name="Payload">Class-specific bytes, encoded by the wire codec.</param>
/// <param name="Hlc">The write's clock stamp.</param>
public sealed record OfflineWrite(
    Guid WriteId,
    Guid DeviceId,
    long DeviceSequence,
    OfflineDataClass DataClass,
    string Target,
    string? BaseVersion,
    ReadOnlyMemory<byte> Payload,
    Hlc Hlc);

/// <summary>One device's writes, in <see cref="OfflineWrite.DeviceSequence"/> order.</summary>
public sealed record OfflineWriteBatch(Guid DeviceId, IReadOnlyList<OfflineWrite> Writes);

/// <summary>What happened to a write (doc 56 section 3).</summary>
public enum OfflineOutcomeKind
{
    /// <summary>Applied.</summary>
    Applied = 1,
    /// <summary>Same write id seen before; nothing changed.</summary>
    Duplicate = 2,
    /// <summary>Base version no longer current; a review item was opened.</summary>
    Conflict = 3,
    /// <summary>Concurrent GC edit: both versions kept, the higher server sequence is current.</summary>
    ConcurrentEdit = 4,
    /// <summary>Held for human review (clock skew, safety-relevant preference).</summary>
    HeldForReview = 5,
    /// <summary>Rejected with a reason.</summary>
    Rejected = 6,
    /// <summary>No module handles this data class on this server.</summary>
    ModuleUnavailable = 7,
}

/// <summary>The result for one write.</summary>
/// <param name="WriteId">The write this is for.</param>
/// <param name="Kind">The outcome.</param>
/// <param name="ServerVersion">The server's version after the merge, if any.</param>
/// <param name="Reason">Human-readable reason for non-applied outcomes.</param>
public sealed record OfflineWriteOutcome(Guid WriteId, OfflineOutcomeKind Kind, string? ServerVersion = null, string? Reason = null);

/// <summary>The result of a batch: outcomes in order, the highest applied device sequence, and a gap if one stopped the batch.</summary>
/// <param name="Outcomes">One outcome per processed write, in order.</param>
/// <param name="AckedSequence">Highest device sequence the server has now recorded for this device.</param>
/// <param name="GapAtSequence">The first missing sequence if the batch stopped at a gap; otherwise null.</param>
public sealed record OfflineBatchResult(IReadOnlyList<OfflineWriteOutcome> Outcomes, long AckedSequence, long? GapAtSequence);
