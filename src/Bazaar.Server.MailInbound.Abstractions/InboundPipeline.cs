namespace Bazaar.Server.MailInbound;

/// <summary>What happened to one message in one pass.</summary>
public enum InboundDisposition
{
    /// <summary>Posted to the inbox GC, read by a reader and handed over.</summary>
    Delivered,

    /// <summary>Stored and clean; announcing and triage follow in the same pass or a later one.</summary>
    Stored,

    /// <summary>Held by a screen, a parse limit or a reader escalation.</summary>
    Quarantined,

    /// <summary>Seen before; this delivery added nothing.</summary>
    Duplicate,

}

/// <summary>A failed step. Carries a code and a hash of the back end id, never an address, a body or an exception message.</summary>
/// <param name="Code">The step that failed, for example <c>backend_unavailable</c> or an exception type name.</param>
/// <param name="Ref">A short hash of the back end's delivery id, or null when the failure is not about one message.</param>
public sealed record InboundFailure(string Code, string? Ref);

/// <summary>
/// The result for one mailbox in one pass. Nothing is dropped silently: every pulled message is stored (counted in <paramref name="Pulled"/>) and either
/// finished, a duplicate, or still pending; <paramref name="Deferred"/> is the stored messages left for a later pass.
/// </summary>
public sealed record MailboxPassResult(
    Guid MailboxId,
    int Pulled,
    int Delivered,
    int Quarantined,
    int Duplicates,
    int Deferred,
    IReadOnlyList<InboundFailure> Failures)
{
    public bool IsClean => Failures.Count == 0;
}

/// <summary>The result of a pass over every mailbox.</summary>
public sealed record InboundPassResult(IReadOnlyList<MailboxPassResult> Mailboxes)
{
    public int Failures => Mailboxes.Sum(m => m.Failures.Count);
}

/// <summary>An event for the host's log. Closed fields only: ids, codes and counts. No address, subject or body can appear.</summary>
public sealed record InboundEvent(string Kind, Guid? MailboxId, Guid? MailMessageId, string? Code);

/// <summary>Receives redacted events. The host adapts it to its logger; the default does nothing.</summary>
public interface IInboundMailEvents
{
    void Report(InboundEvent inboundEvent);
}

/// <summary>Processes one mailbox: pull, dedupe, parse, screen, hold or deliver (doc 22 inbound steps 1 to 7).</summary>
public interface IInboundMailPipeline
{
    /// <summary>One bounded pass over one mailbox. Back end and step failures come back in the result; only cancellation throws.</summary>
    Task<MailboxPassResult> ProcessMailboxAsync(InboundMailboxBinding mailbox, CancellationToken ct);
}

/// <summary>
/// The pass over all mailboxes. A host scheduler (the jobs module, once it lands) calls <see cref="RunPassAsync"/> on an interval; this
/// module registers no background service.
/// </summary>
public interface IInboundMailWorker
{
    Task<InboundPassResult> RunPassAsync(CancellationToken ct);
}
