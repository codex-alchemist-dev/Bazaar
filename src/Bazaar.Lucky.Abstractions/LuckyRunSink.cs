namespace Bazaar.Lucky;

/// <summary>The brainstorm GC as opened by the host.</summary>
/// <param name="GcId">The new GC.</param>
/// <param name="OpeningSeq">The sequence number of the GC's opening message. Inbox rows are keyed by this (doc 06).</param>
public sealed record LuckyGcOpened(Guid GcId, long OpeningSeq);

/// <summary>An idea entry as the host stored it.</summary>
/// <param name="IdeaIndex">The idea's index in the run.</param>
/// <param name="MessageId">The stored message's id, which a card points back to.</param>
public sealed record LuckyStoredEntry(int IdeaIndex, Guid MessageId);

/// <summary>
/// The side effects a Lucky run needs from the host (doc 26, section 6). The module plans them and applies them through this
/// port. The host implements it with the GC, inbox and Kanban services. Those implementations are not in this module.
/// </summary>
public interface ILuckyRunSink
{
    /// <summary>Opens the run's ephemeral brainstorm GC and writes its opening message.</summary>
    Task<LuckyGcOpened> OpenGcAsync(LuckyRunPlan plan, CancellationToken ct);

    /// <summary>Appends the idea entries to the GC, in order, and returns where each one was stored.</summary>
    Task<IReadOnlyList<LuckyStoredEntry>> AppendEntriesAsync(Guid gcId, IReadOnlyList<LuckyGcEntry> entries, CancellationToken ct);

    /// <summary>Enqueues one brainstorm inbox row per wake, keyed to the opening message. Returns how many rows were inserted.</summary>
    Task<int> EnqueueWakesAsync(Guid gcId, long openingSeq, IReadOnlyList<LuckyWake> wakes, CancellationToken ct);

    /// <summary>Creates one Backlog card from a stored idea message. Returns the card id.</summary>
    Task<Guid> CreateCardAsync(Guid projectId, Guid gcId, Guid messageId, string title, CancellationToken ct);
}
