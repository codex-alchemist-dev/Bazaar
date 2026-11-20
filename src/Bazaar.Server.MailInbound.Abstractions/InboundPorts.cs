namespace Bazaar.Server.MailInbound;

// Thin ports to systems owned by other modules (docs/design/01 GC log, 15 reader sessions and taint). See InboundContracts.cs for the
// replacement note. Every port is idempotent where it writes, because the pipeline is at-least-once.

/// <summary>
/// The announcement the pipeline posts for a clean message. Fixed shape: sender domain, time, screen codes and the mail reference.
/// There is no field for the subject, the display name, the address or any body text, so none can be posted.
/// </summary>
/// <param name="InboxGcId">The agent's inbox GC.</param>
/// <param name="MailboxId">The receiving mailbox.</param>
/// <param name="MailMessageId">The <c>mail_message</c> reference; also the idempotency key.</param>
/// <param name="SenderDomain">The sender's domain.</param>
/// <param name="ReceivedAt">When the message arrived.</param>
/// <param name="ScreenCodes">Screen codes that applied but did not hold the message (always empty today: any code holds).</param>
public sealed record ExternalMailEntry(
    Guid InboxGcId,
    Guid MailboxId,
    Guid MailMessageId,
    string SenderDomain,
    DateTimeOffset ReceivedAt,
    IReadOnlyList<string> ScreenCodes)
{
    /// <summary>The GC message kind (registered in doc 01's type registry).</summary>
    public const string Kind = "external_mail";

    /// <summary>The provenance value (doc 15, C20). Always external for inbound mail.</summary>
    public const string SourceTrust = "external";
}

/// <summary>
/// What an agent may learn about held mail: that some arrived and how many are held. The sender is not named, because
/// a sender domain is attacker-controlled text (doc 22: "agents see a count only").
/// </summary>
/// <param name="InboxGcId">The agent's inbox GC.</param>
/// <param name="MailMessageId">The held message's reference; also the idempotency key.</param>
/// <param name="ReasonCodes">Closed screen reason names from the pipeline, never sender text.</param>
/// <param name="HeldCount">How many messages are held now.</param>
public sealed record QuarantineNotice(Guid InboxGcId, Guid MailMessageId, IReadOnlyList<string> ReasonCodes, int HeldCount)
{
    /// <summary>The GC message kind for the count and metadata entry.</summary>
    public const string Kind = "mail_quarantine_notice";
}

/// <summary>The only path from the inbound pipeline into a GC log. Posts are system messages and idempotent on the mail message id.</summary>
public interface IInboxGcPoster
{
    /// <summary>Posts the <c>external_mail</c> message (<c>source_trust = external</c>) and returns its GC message id. A repeat returns the first id.</summary>
    Task<Guid> PostExternalMailAsync(ExternalMailEntry entry, CancellationToken ct);

    /// <summary>Posts the count and metadata entry for held mail. A repeat is a no-op.</summary>
    Task PostQuarantineNoticeAsync(QuarantineNotice notice, CancellationToken ct);

    /// <summary>Posts an accepted <c>mail_triage</c> hand-over as its own message. A repeat is a no-op.</summary>
    Task PostTriageHandoverAsync(Guid inboxGcId, MailTriageHandover handover, CancellationToken ct);
}

/// <summary>What a reader session is started for.</summary>
public sealed record ReaderSessionRequest(Guid MailMessageId, Guid MailboxId, Guid PersonId);

/// <summary>A started reader session.</summary>
public sealed record ReaderSessionHandle(Guid SessionId);

/// <summary>Starts and ends reader sessions (doc 15's reader primitive: tool-less, hard-tainted). One session per message.</summary>
public interface IMailReaderSessions
{
    Task<ReaderSessionHandle> StartAsync(ReaderSessionRequest request, CancellationToken ct);

    Task EndAsync(ReaderSessionHandle session, CancellationToken ct);
}

/// <summary>The taint port (doc 15). Hard taint only goes up; nothing here lowers it.</summary>
public interface IReaderSessionTaint
{
    /// <summary>Marks the session hard-tainted because it is about to read external content.</summary>
    Task MarkHardAsync(Guid sessionId, Guid mailMessageId, CancellationToken ct);
}

/// <summary>What the reader model is shown. The only place raw text leaves the pipeline, and only into a hard-tainted, tool-less session.</summary>
public sealed record MailReaderInput(
    Guid MailMessageId,
    string? FromDisplayName,
    string? Subject,
    string? TextBody,
    string? HtmlBody,
    IReadOnlyList<string> AttachmentNames);

/// <summary>
/// The raw model call behind the triage reader (doc 05 gateway). It returns the model's <c>mail_triage</c> payload as text, or null
/// when there was none; the reader checks it against the schema and treats it as untrusted.
/// </summary>
public interface IMailTriageModel
{
    Task<string?> CompleteAsync(ReaderSessionHandle session, MailReaderInput input, CancellationToken ct);
}

/// <summary>The reader model call (doc 05 gateway, behind a port so tests run a fake). It can answer with a hand-over and nothing else.</summary>
public interface IMailTriageReader
{
    Task<MailTriageHandover> TriageAsync(ReaderSessionHandle session, MailReaderInput input, CancellationToken ct);
}
