using Bazaar.Server.Mail;

namespace Bazaar.Server.MailInbound;

// Inbound pipeline contracts (docs/design/22-email-subsystem.md, "Inbound pipeline module (WP4 slice)"). The pipeline reuses the
// existing mail contracts (IMailBackend, IMailQuarantineStore) and reaches the GC log, reader sessions and taint only through the
// thin ports below. TODO(ports): replace the GC, reader and taint ports with Bazaar.GC.Abstractions / Bazaar.Server.Security.Abstractions
// types once PR 743 and a GC writer contract in an abstractions project land. No new GC, taint, reader or approval mechanism lives here.

/// <summary>One mailbox the worker pulls: its back end, the agent person whose key seals the body, and the agent's inbox GC.</summary>
/// <param name="MailboxId">The <c>mail_mailbox</c> id.</param>
/// <param name="PersonId">The person whose key encrypts stored bodies (doc 22, quarantine and erasure).</param>
/// <param name="InboxGcId">The agent's inbox GC (doc 01).</param>
/// <param name="Box">The address reference the back end pulls.</param>
/// <param name="Backend">The back end for this mailbox's domain.</param>
public sealed record InboundMailboxBinding(Guid MailboxId, Guid PersonId, Guid InboxGcId, MailboxRef Box, IMailBackend Backend);

/// <summary>Lists the mailboxes to pull. The host implements it over <c>mail_mailbox</c> and <c>mail_domain</c>.</summary>
public interface IInboundMailboxDirectory
{
    Task<IReadOnlyList<InboundMailboxBinding>> ListActiveAsync(CancellationToken ct);
}

/// <summary>A message body after per-person encryption. The pipeline never stores the clear text.</summary>
public sealed record SealedBody(Guid KeyId, byte[] Ciphertext, byte[] Nonce);

/// <summary>Encrypts a raw message under a person's key (doc 14, erasure keys). The host supplies the real implementation.</summary>
public interface IMailBodySealer
{
    SealedBody Seal(Guid personId, ReadOnlyMemory<byte> raw);

    /// <summary>
    /// Decrypts a stored body so a pending message can be resumed and shown to a reader session. The key service behind it decides who may call it;
    /// the pipeline calls it only to feed a hard-tainted, tool-less reader.
    /// </summary>
    byte[] Open(Guid personId, SealedBody sealedBody);
}

/// <summary>Where a stored inbound message is. <c>Received</c> is the only state the pipeline resumes from.</summary>
public enum InboundStoreState
{
    /// <summary>Stored, not yet held or delivered. A crash leaves rows here; the next delivery of the same message resumes it.</summary>
    Received,

    /// <summary>Admitted to quarantine (held, or later released, purged or reported).</summary>
    Held,

    /// <summary>Posted to the inbox GC and triaged.</summary>
    Delivered,
}

/// <summary>A new inbound row. The sender is kept as a domain only; no plaintext address or body is stored in clear.</summary>
/// <param name="Id">The new <c>mail_message</c> id.</param>
/// <param name="MailboxId">The receiving mailbox.</param>
/// <param name="RfcMessageId">The Message-ID header value without angle brackets, or null when absent or unparseable.</param>
/// <param name="BackendId">The back end's own id for the delivery.</param>
/// <param name="SenderDomain">The sender's domain, lowercased, or the unknown-sender placeholder.</param>
/// <param name="AuthSummary">A short auth summary such as <c>spf=pass dkim=none dmarc=none</c>.</param>
/// <param name="ScreenCodes">Reason code names from the screens; empty for a clean message.</param>
/// <param name="ReceivedAt">When the back end received it.</param>
/// <param name="Body">The encrypted raw message.</param>
public sealed record NewInboundMessage(
    Guid Id,
    Guid MailboxId,
    string? RfcMessageId,
    string BackendId,
    string SenderDomain,
    string AuthSummary,
    IReadOnlyList<string> ScreenCodes,
    DateTimeOffset ReceivedAt,
    SealedBody Body);

/// <summary>
/// A stored message the pipeline has not finished (state <c>received</c>). <paramref name="ScreenCodes"/> is empty for a clean message and names the reasons
/// for one a screen held but a crash kept from reaching quarantine. The body is still encrypted.
/// </summary>
public sealed record PendingInboundMessage(
    Guid Id, string SenderDomain, string AuthSummary, IReadOnlyList<string> ScreenCodes, DateTimeOffset ReceivedAt, SealedBody Body);

/// <summary>Whether an insert created a row or found the same delivery already stored.</summary>
public enum InboundInsertOutcome
{
    Inserted,
    Duplicate,
}

/// <summary>The result of an insert. For a duplicate, <paramref name="Id"/> is the stored row's id and the state and GC message are the stored ones.</summary>
public sealed record InboundInsertResult(InboundInsertOutcome Outcome, Guid Id, InboundStoreState State, Guid? GcMessageId);

/// <summary>Who asks to read a stored raw body.</summary>
public enum MailAccessorKind
{
    /// <summary>A reader session: tool-less and always hard-tainted.</summary>
    Reader,

    /// <summary>The owner, through the owner-signed path.</summary>
    Owner,

    /// <summary>An actor agent session. It never reads raw mail (doc 22, C18).</summary>
    Agent,

    /// <summary>The server's own pipeline and jobs. They move bodies in and out encrypted and never read them.</summary>
    System,
}

/// <summary>The caller of <see cref="IMailMessageStore.ReadSealedBodyAsync"/>.</summary>
public sealed record MailAccessor(MailAccessorKind Kind)
{
    public static MailAccessor Reader { get; } = new(MailAccessorKind.Reader);

    public static MailAccessor Owner { get; } = new(MailAccessorKind.Owner);

    public static MailAccessor Agent { get; } = new(MailAccessorKind.Agent);

    public static MailAccessor System { get; } = new(MailAccessorKind.System);
}

/// <summary>Why a body read was answered or refused.</summary>
public enum MailBodyReadOutcome
{
    Ok,
    Denied,
    NotFound,
}

/// <summary>A body read. <paramref name="Body"/> is set only when <paramref name="Outcome"/> is <see cref="MailBodyReadOutcome.Ok"/>.</summary>
public sealed record MailBodyReadResult(MailBodyReadOutcome Outcome, SealedBody? Body);

/// <summary>
/// Persistence for inbound <c>mail_message</c> rows. No method returns a clear body, a full sender address or free-form sender text;
/// the only body read is <see cref="ReadSealedBodyAsync"/>, which refuses an actor.
/// </summary>
public interface IMailMessageStore
{
    /// <summary>
    /// Stores a message in state <c>received</c>, or reports the existing row when the delivery was seen before: the same
    /// <c>(mailbox, rfc_message_id)</c> or the same <c>(mailbox, backend_id)</c>. Duplicate delivery is normal (doc 22).
    /// </summary>
    Task<InboundInsertResult> InsertReceivedAsync(NewInboundMessage message, CancellationToken ct);

    /// <summary>Records the inbox GC message that announced the mail. Idempotent. The state stays <c>received</c> until triage is done.</summary>
    Task RecordGcMessageAsync(Guid id, Guid gcMessageId, CancellationToken ct);

    /// <summary>
    /// Messages stored but not yet held, or announced and triaged, oldest first (received time, then id). Back ends such as IMAP mark mail seen when it is pulled,
    /// so a stored row, not a redelivery, is what makes a crashed or deferred message come back. Bodies stay encrypted.
    /// </summary>
    Task<IReadOnlyList<PendingInboundMessage>> ListPendingAsync(Guid mailboxId, int limit, CancellationToken ct);

    /// <summary>How many messages <see cref="ListPendingAsync"/> would return without a limit.</summary>
    Task<int> CountPendingAsync(Guid mailboxId, CancellationToken ct);

    /// <summary>Moves a <c>received</c> row to <c>delivered</c>. Returns false when the row is no longer in <c>received</c>.</summary>
    Task<bool> MarkDeliveredAsync(Guid id, CancellationToken ct);

    /// <summary>The encrypted body, for a reader or the owner only. An agent or the system gets <see cref="MailBodyReadOutcome.Denied"/>.</summary>
    Task<MailBodyReadResult> ReadSealedBodyAsync(Guid id, MailAccessor accessor, CancellationToken ct);
}
