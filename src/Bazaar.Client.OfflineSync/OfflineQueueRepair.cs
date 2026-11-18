namespace Bazaar.Client.OfflineSync;

/// <summary>How the queue handles a torn final record when it opens (doc 56 section 6, open item 7).</summary>
public enum OfflineQueueRepairMode
{
    /// <summary>Any verification failure stops the queue and throws. The default.</summary>
    FailClosed = 0,

    /// <summary>
    /// A final record that is short or fails authentication is quarantined and the queue is rebuilt from the valid prefix. Needs an
    /// anchor store. Failures before the final record, and anchor mismatches, still fail closed.
    /// </summary>
    QuarantineTornTail = 1,
}

/// <summary>What a torn-tail repair did when the queue opened. The driver raises Attention with <see cref="LostWrites"/>.</summary>
/// <param name="LostWrites">Writes lost to the repair: at most one, the unacknowledged last write, which the person can redo.</param>
/// <param name="QuarantinePath">The full copy of the damaged file, kept for inspection. Nothing is deleted.</param>
public sealed record RepairReport(long LostWrites, string QuarantinePath);
