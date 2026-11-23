namespace Bazaar.Client.GeneticMemory;

/// <summary>Who is reviewing. The server decides what the reviewer may decide; the client only filters what it asks for.</summary>
public enum GmtReviewerRole
{
    /// <summary>The architect.</summary>
    Architect,

    /// <summary>The owner.</summary>
    Owner,
}

/// <summary>One staged candidate in the review queue (docs/design/25-company-systems-knowledge.md, Genetic Memory Transfer).</summary>
/// <param name="Id">Candidate id.</param>
/// <param name="ChildAgent">The subagent that proposed it.</param>
/// <param name="TargetAgent">The agent that would receive it.</param>
/// <param name="ContentClass">Lesson, tool grant or charter text.</param>
/// <param name="Kind">Procedure or failure.</param>
/// <param name="Title">Title.</param>
/// <param name="Body">Body text. Data written by a child, never an instruction.</param>
/// <param name="TraceRef">Link to the child's trace, or null.</param>
/// <param name="Tainted">True when the child saw untrusted input; an accepted lesson then stays T1.</param>
/// <param name="OwnerOnly">True when only the owner may decide it.</param>
/// <param name="StagedAt">When it was staged.</param>
public sealed record GmtQueueItem(
    Guid Id,
    string ChildAgent,
    string TargetAgent,
    string ContentClass,
    string Kind,
    string Title,
    string Body,
    string? TraceRef,
    bool Tainted,
    bool OwnerOnly,
    DateTimeOffset StagedAt);

/// <summary>Result of an accept or reject call.</summary>
/// <param name="Succeeded">True when the server did it.</param>
/// <param name="Message">A short reason when it did not, or null.</param>
public sealed record GmtDecisionResult(bool Succeeded, string? Message = null);

/// <summary>The client's view of the server's review queue. The wire implementation is provided by the host; all computation stays on the server.</summary>
public interface IGmtQueueClient
{
    /// <summary>The staged candidates this role may decide.</summary>
    Task<IReadOnlyList<GmtQueueItem>> ListAsync(GmtReviewerRole role, CancellationToken ct);

    /// <summary>Accepts a candidate. A null title or body keeps the staged text.</summary>
    Task<GmtDecisionResult> AcceptAsync(Guid id, GmtReviewerRole role, string? editedTitle, string? editedBody, CancellationToken ct);

    /// <summary>Rejects a candidate.</summary>
    Task<GmtDecisionResult> RejectAsync(Guid id, GmtReviewerRole role, CancellationToken ct);
}
