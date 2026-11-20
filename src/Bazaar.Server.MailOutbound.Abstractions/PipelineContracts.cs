using Bazaar.Server.Mail;

namespace Bazaar.Server.MailOutbound;

// Outbound pipeline contracts (docs/design/22-email-subsystem.md, "Outbound pipeline (WP6)"). The pipeline is the caller of
// IOutboundMailPolicy. It owns no gate and no counter: approvals, rate limits and audit are reached through ports onto the existing modules.

/// <summary>Where a stored outbound send stands. Mirrors <c>mail_outbound.status</c> (mail 0021 and 0023).</summary>
public enum OutboundStatus
{
    /// <summary>Tier 2: held until the owner decides. Nothing was sent.</summary>
    AwaitingApproval,

    /// <summary>A send failed and waits for its next attempt (<see cref="StoredOutbound.NextAttemptAt"/>).</summary>
    Pending,

    /// <summary>Stored before the back end call. A row left here after a restart is resumed.</summary>
    Sending,

    /// <summary>The back end accepted it. Terminal.</summary>
    Sent,

    /// <summary>Every attempt failed. Terminal.</summary>
    Failed,

    /// <summary>The recipient's server bounced it (reported by the inbound pipeline). Terminal.</summary>
    Bounced,

    /// <summary>The owner refused the approval. Terminal.</summary>
    Rejected,

    /// <summary>No decision arrived before the approval expired. Terminal.</summary>
    Expired,
}

/// <summary>What the caller learns about one send.</summary>
public enum OutboundSendStatus
{
    /// <summary>Sent now.</summary>
    Sent,

    /// <summary>The key was seen before and the message had already been sent. Nothing was sent again.</summary>
    AlreadySent,

    /// <summary>The policy refused it. Nothing was stored or sent.</summary>
    Refused,

    /// <summary>The policy paused the mailbox (a rate limit). Nothing was stored; try again after <see cref="OutboundSendResult.RetryAfter"/>.</summary>
    Paused,

    /// <summary>Held for the owner. The approval id is in the result.</summary>
    AwaitingApproval,

    /// <summary>A send failed and is scheduled again after <see cref="OutboundSendResult.RetryAfter"/>.</summary>
    RetryScheduled,

    /// <summary>The owner refused. Terminal.</summary>
    Rejected,

    /// <summary>The approval expired. Terminal.</summary>
    Expired,

    /// <summary>Every attempt failed. Terminal.</summary>
    Failed,

    /// <summary>The message bounced. Terminal.</summary>
    Bounced,
}

/// <summary>The pipeline's answer to one send.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Reason">The policy's reason code (Refuse, Pause, approval); null when the policy gave none.</param>
/// <param name="IdempotencyKey">The derived key, or empty when the message was refused or paused.</param>
/// <param name="MessageId">The stored <c>mail_message</c> id, once one exists.</param>
/// <param name="ApprovalId">The approval raised for this message (never shared with another message).</param>
/// <param name="RetryAfter">For Paused and RetryScheduled: how long to wait. Zero otherwise.</param>
/// <param name="Receipt">The back end's receipt once sent.</param>
/// <param name="Attempts">Send attempts made so far.</param>
public sealed record OutboundSendResult(
    OutboundSendStatus Status,
    OutboundReason? Reason,
    string IdempotencyKey,
    Guid? MessageId = null,
    Guid? ApprovalId = null,
    TimeSpan RetryAfter = default,
    SendReceipt? Receipt = null,
    int Attempts = 0);

/// <summary>The outbound pipeline (doc 22, outbound flow).</summary>
public interface IOutboundMailPipeline
{
    /// <summary>
    /// Sends one message through the policy. A key already stored is never re-evaluated (evaluation consumes a quota slot) and never sent twice:
    /// the stored send is advanced instead.
    /// </summary>
    Task<OutboundSendResult> SendAsync(OutboundRequest request, CancellationToken ct);

    /// <summary>
    /// Advances one stored send: resolves its approval, retries it when due, or completes a send a restart interrupted. Null when no such key exists.
    /// </summary>
    Task<OutboundSendResult?> ResumeAsync(string idempotencyKey, CancellationToken ct);

    /// <summary>Advances every stored send that is due, at most <paramref name="max"/>. Returns how many were advanced.</summary>
    Task<int> ResumeDueAsync(int max, CancellationToken ct);

    /// <summary>
    /// The hook for the inbound pipeline, which owns bounce ingestion: marks a sent message bounced. False when the key is unknown or not in the Sent state.
    /// </summary>
    Task<bool> RecordBounceAsync(string idempotencyKey, CancellationToken ct);
}

/// <summary>One stored send: a <c>mail_message</c> row (state sending, queued, sent or failed) joined to its <c>mail_outbound</c> receipt row.</summary>
/// <param name="IdempotencyKey">The key. A repeated key sends once.</param>
/// <param name="MessageId">The <c>mail_message</c> id.</param>
/// <param name="ApprovalId">The approval raised for this message, if any.</param>
/// <param name="Status">Where it stands.</param>
/// <param name="Attempts">Send attempts so far.</param>
/// <param name="NextAttemptAt">For Pending: when the next attempt is due.</param>
/// <param name="Footer">The footer the policy resolved, kept so a resumed send uses it and never a recomputed value.</param>
/// <param name="Tier">1 or 2, as the policy decided.</param>
/// <param name="Reason">The policy's reason code.</param>
/// <param name="UpdatedAt">Last change.</param>
/// <param name="Request">The request, or null once a terminal failure cleared the stored text.</param>
/// <param name="BackendMessageId">The back end's message id once sent.</param>
public sealed record StoredOutbound(
    string IdempotencyKey,
    Guid MessageId,
    Guid? ApprovalId,
    OutboundStatus Status,
    int Attempts,
    DateTimeOffset? NextAttemptAt,
    string Footer,
    int Tier,
    OutboundReason Reason,
    DateTimeOffset UpdatedAt,
    OutboundRequest? Request,
    string? BackendMessageId = null);

/// <summary>The durable outbound store (doc 22, durable outbound store option (a)).</summary>
public interface IOutboundMailStore
{
    /// <summary>The stored send for a key, or null.</summary>
    Task<StoredOutbound?> FindAsync(string idempotencyKey, CancellationToken ct);

    /// <summary>
    /// Stores a new send atomically: the <c>mail_message</c> row first, then the <c>mail_outbound</c> row. When the key already exists nothing is
    /// written and the existing row comes back with <c>Created</c> false, so two racing callers cannot both create it.
    /// </summary>
    Task<(StoredOutbound Record, bool Created)> TryInsertAsync(StoredOutbound record, CancellationToken ct);

    /// <summary>
    /// Replaces a row only if its stored status and attempts still equal those of <paramref name="expected"/> (compare-and-set), so two resumers
    /// cannot both claim one send. Returns false when another change got there first.
    /// </summary>
    Task<bool> TryUpdateAsync(StoredOutbound expected, StoredOutbound updated, CancellationToken ct);

    /// <summary>
    /// Keys of sends that need attention: awaiting approval, pending with a due attempt, or sending and untouched since
    /// <paramref name="staleSendingBefore"/> (a send a restart interrupted).
    /// </summary>
    Task<IReadOnlyList<string>> ListDueAsync(DateTimeOffset now, DateTimeOffset staleSendingBefore, int max, CancellationToken ct);
}

/// <summary>Lifecycle of one approval as the pipeline needs it. Mirrors <c>ApprovalStatus</c> of the approvals store.</summary>
public enum MailApprovalStatus
{
    /// <summary>Waiting for the owner.</summary>
    Pending,

    /// <summary>Approved for this exact message.</summary>
    Approved,

    /// <summary>The owner refused.</summary>
    Denied,

    /// <summary>No decision arrived in time.</summary>
    Expired,

    /// <summary>The approved send ran. The grant cannot be used again.</summary>
    Consumed,
}

/// <summary>What the owner's approval card shows for one outbound message (doc 22, client mail screens).</summary>
/// <param name="AgentId">The sending agent.</param>
/// <param name="IdempotencyKey">The message's key. The approval is bound to it and to nothing else.</param>
/// <param name="Tier">2.</param>
/// <param name="Reason">Why approval is needed.</param>
/// <param name="From">The server-set From mailbox.</param>
/// <param name="Recipients">The recipients.</param>
/// <param name="Subject">The subject.</param>
/// <param name="TextBody">The body, before the footer.</param>
/// <param name="FooterPreview">The footer that will be appended.</param>
/// <param name="AttachmentCount">The number of attachments.</param>
public sealed record MailApprovalRequest(
    string AgentId,
    string IdempotencyKey,
    int Tier,
    OutboundReason Reason,
    MailboxRef From,
    IReadOnlyList<string> Recipients,
    string Subject,
    string TextBody,
    string FooterPreview,
    int AttachmentCount);

/// <summary>
/// Port onto the approvals store (doc 15): one approval per message, never carried over. The host adapter maps these calls to
/// the approval service; this module adds no approval mechanism.
/// </summary>
public interface IMailApprovalGateway
{
    /// <summary>Raises a new approval request for exactly this message and returns its id.</summary>
    Task<Guid> RequestAsync(MailApprovalRequest request, CancellationToken ct);

    /// <summary>The approval's current status.</summary>
    Task<MailApprovalStatus> GetStatusAsync(Guid approvalId, CancellationToken ct);

    /// <summary>Marks an approved grant consumed. False when it was not approved or was already consumed.</summary>
    Task<bool> ConsumeAsync(Guid approvalId, CancellationToken ct);
}

/// <summary>
/// Thrown by <see cref="IMailApprovalGateway.RequestAsync"/> when the approvals store refuses to take the request. Nothing was filed. The pipeline ends
/// the send as failed and never asks again. The reason code is a short lower-case code (letters, digits, <c>_</c>, <c>:</c>, <c>.</c>, <c>-</c>, at most
/// <see cref="MaxReasonCodeLength"/> characters); it never carries mail content or an address.
/// </summary>
public sealed class MailApprovalRefusedException : InvalidOperationException
{
    /// <summary>The longest reason code kept.</summary>
    public const int MaxReasonCodeLength = 64;

    /// <summary>The code used when the supplied one is empty or not a plain code.</summary>
    public const string DefaultReasonCode = "approval_refused";

    public MailApprovalRefusedException(string reasonCode, Exception? inner = null)
        : base("The approvals queue refused the mail approval request.", inner) => ReasonCode = Sanitize(reasonCode);

    /// <summary>A bounded, non-sensitive code for the audit record.</summary>
    public string ReasonCode { get; }

    private static string Sanitize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return DefaultReasonCode;
        var trimmed = code.Length > MaxReasonCodeLength ? code[..MaxReasonCodeLength] : code;
        foreach (var c in trimmed)
        {
            var plain = c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or ':' or '.' or '-';
            if (!plain) return DefaultReasonCode;
        }

        return trimmed;
    }
}

/// <summary>
/// What the owner is told when a send dies because the approvals store refused its request (PROPOSED, doc 22). Ids, a bounded reason code, the sender
/// domain, a recipient count and a time. No subject, body, display name or address (not even a local part) has a field here.
/// </summary>
public sealed record OutboundRefusalNotice(
    string AgentId,
    Guid MessageId,
    string ReasonCode,
    string SenderDomain,
    int RecipientCount,
    DateTimeOffset At);

/// <summary>
/// Port: tells the owner about an outbound send that ended as failed because its approval request was refused. The default is no notice (the
/// <c>mail.outbound.failed</c> audit row stays either way). A notice that throws never changes the send's terminal state.
/// </summary>
public interface IOutboundMailNotices
{
    Task PostApprovalRefusedAsync(OutboundRefusalNotice notice, CancellationToken ct);
}

/// <summary>The fail-closed default: no notice.</summary>
public sealed class NoOutboundMailNotices : IOutboundMailNotices
{
    public Task PostApprovalRefusedAsync(OutboundRefusalNotice notice, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Port: the back end that sends for one operator domain.</summary>
public interface IMailBackendResolver
{
    /// <summary>The back end for the domain, or null when none is configured.</summary>
    IMailBackend? Resolve(string domain);
}

/// <summary>An encrypted body as <c>mail_message</c> keeps it (<c>body_key_id</c>, <c>body_ct</c>, <c>body_nonce</c>).</summary>
public sealed record ProtectedBody(Guid KeyId, byte[] Ciphertext, byte[] Nonce);

/// <summary>Port: the per-person encryption of stored message text (doc 04 pattern). The store never writes plaintext.</summary>
public interface IMailBodyProtector
{
    ValueTask<ProtectedBody> ProtectAsync(string agentId, ReadOnlyMemory<byte> plaintext, CancellationToken ct);

    ValueTask<byte[]> UnprotectAsync(ProtectedBody body, CancellationToken ct);
}

/// <summary>Pipeline settings (PROPOSED: <c>mail.outbound.max_attempts</c>, <c>mail.outbound.retry_base_seconds</c>, <c>mail.outbound.retry_max_seconds</c>).</summary>
/// <param name="MaxAttempts">Send attempts before a send fails for good. Kept between 1 and 10.</param>
/// <param name="RetryBaseDelay">The delay after the first failed attempt; each later one doubles it. Zero or less means 30 seconds.</param>
/// <param name="RetryMaxDelay">The ceiling for the delay. Zero or less means one hour.</param>
/// <param name="SendingStaleAfter">How long a row may sit in Sending before a resumer treats it as interrupted. Zero or less means five minutes.</param>
public sealed record OutboundPipelineOptions(
    int MaxAttempts = 5,
    TimeSpan RetryBaseDelay = default,
    TimeSpan RetryMaxDelay = default,
    TimeSpan SendingStaleAfter = default)
{
    public TimeSpan EffectiveBaseDelay => RetryBaseDelay > TimeSpan.Zero ? RetryBaseDelay : TimeSpan.FromSeconds(30);

    public TimeSpan EffectiveMaxDelay => RetryMaxDelay > TimeSpan.Zero ? RetryMaxDelay : TimeSpan.FromHours(1);

    public TimeSpan EffectiveStaleAfter => SendingStaleAfter > TimeSpan.Zero ? SendingStaleAfter : TimeSpan.FromMinutes(5);

    /// <summary>The attempt cap, kept between 1 and 10 so a setting cannot make a send retry forever.</summary>
    public int EffectiveMaxAttempts => Math.Clamp(MaxAttempts, 1, 10);

    /// <summary>The delay before the next attempt after <paramref name="failedAttempts"/> failures: base times two to the (n-1), capped.</summary>
    public TimeSpan DelayAfter(int failedAttempts)
    {
        var exponent = Math.Clamp(failedAttempts - 1, 0, 20);
        var ticks = EffectiveBaseDelay.Ticks * (1L << exponent);
        return TimeSpan.FromTicks(Math.Min(ticks, EffectiveMaxDelay.Ticks));
    }
}
