namespace Bazaar.Kodo.Sentinel;

/// <summary>One sentinel event for the Kodo journal. The journal encodes and chains it; the sentinel only names it.</summary>
/// <param name="At">When the event happened, in UTC.</param>
/// <param name="Kind">The event kind, for example <see cref="RollbackKind"/>.</param>
/// <param name="FromSlot">The slot that was left, or null when no slot id was known.</param>
/// <param name="ToSlot">The slot that is now active.</param>
/// <param name="Cause">The cause code from <see cref="SentinelCauses"/>.</param>
/// <param name="Detail">A short detail for the reader, or null.</param>
public sealed record SentinelJournalEvent(DateTimeOffset At, string Kind, string? FromSlot, string? ToSlot, string Cause, string? Detail)
{
    /// <summary>The kind written when the sentinel rolls back to the previous slot.</summary>
    public const string RollbackKind = "sentinel.rollback";
}

/// <summary>
/// The journal seam for the sentinel (doc 46 section 6.3). A rollback appends its event here before the flip is announced.
/// The implementation throws <see cref="IOException"/> when the event cannot be written.
/// </summary>
public interface ISentinelJournal
{
    /// <summary>Appends one event to the Kodo journal.</summary>
    void Append(SentinelJournalEvent sentinelEvent);
}
