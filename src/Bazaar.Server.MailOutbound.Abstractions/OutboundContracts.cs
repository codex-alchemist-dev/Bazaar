using Bazaar.Server.Mail;

namespace Bazaar.Server.MailOutbound;

// Outbound send policy contracts (docs/design/22-email-subsystem.md, "Outbound policy module (WP6 slice)").
// The policy only maps doc 22's rules onto existing contracts: counters and quotas are Bazaar.RateLimits (IRateLimiter),
// and the caller turns NeedsOwnerApproval into a Bazaar.Approvals request. This module adds no gate and no counter.

/// <summary>
/// Session taint as the policy sees it. A port-local mirror of the Clean, Soft and Hard states (doc 15), so this module does not
/// reference Bazaar.Server. TODO: replace by the Security.Abstractions SessionTaint once PR 743 merges.
/// </summary>
public enum MailTaint
{
    Clean,
    Soft,
    Hard,
}

/// <summary>One outbound send as the mail tool received it. The policy reads it and never changes it.</summary>
/// <param name="AgentId">The sending agent. The From mailbox is the agent's own and is set by the server (doc 22, account model).</param>
/// <param name="From">The agent's mailbox.</param>
/// <param name="Recipients">All recipient addresses (To, Cc and Bcc alike).</param>
/// <param name="Subject">The subject line.</param>
/// <param name="TextBody">The plain text body, before the footer.</param>
/// <param name="AttachmentCount">The number of attachments. Any attachment is Tier 2.</param>
/// <param name="AttachmentBytes">The total size of the attachments.</param>
/// <param name="Taint">The sending session's taint. Hard taint always means Tier 2 (C06).</param>
/// <param name="UsesTemplate">True when the body was built from an approved template. Tier 1 needs it.</param>
/// <param name="DerivedFromInboundMail">True when any text derives from inbound mail. Tier 2.</param>
/// <param name="IsSignUpReply">True when this answers a service sign-up. Tier 2.</param>
/// <param name="Nonce">A caller value that separates two deliberate sends of identical content from a retry of one send.</param>
public sealed record OutboundRequest(
    string AgentId,
    MailboxRef From,
    IReadOnlyList<string> Recipients,
    string Subject,
    string TextBody,
    int AttachmentCount,
    long AttachmentBytes,
    MailTaint Taint,
    bool UsesTemplate,
    bool DerivedFromInboundMail,
    bool IsSignUpReply,
    string Nonce);

/// <summary>What the caller must do with a send.</summary>
public enum OutboundDecisionKind
{
    /// <summary>Send now. Tier 1 sends carry an after-the-fact GC notice.</summary>
    Send,

    /// <summary>Tier 2: hold the send and raise an owner approval request for this exact message. Approvals never carry over.</summary>
    NeedsOwnerApproval,

    /// <summary>Never send this message.</summary>
    Refuse,

    /// <summary>Pause the mailbox and notify the owner (a rate limit denied the send).</summary>
    Pause,
}

/// <summary>Why a decision came out as it did. Stable codes, safe to log and audit.</summary>
public enum OutboundReason
{
    /// <summary>Send: approved recipient and template body, no escalation.</summary>
    Tier1Template,

    /// <summary>Approval: the session is hard-tainted (C06).</summary>
    HardTaint,

    /// <summary>Approval: Bazaar-managed and external recipients together (not refused; the owner decides).</summary>
    MixedBazaarRecipients,

    /// <summary>Approval: at least one recipient is new.</summary>
    NewRecipient,

    /// <summary>Approval: text derives from inbound mail.</summary>
    DerivedFromInboundMail,

    /// <summary>Approval: the message carries an attachment.</summary>
    Attachment,

    /// <summary>Approval: a sign-up reply.</summary>
    SignUpReply,

    /// <summary>Approval: the body is not built from an approved template.</summary>
    NotTemplate,

    /// <summary>Refuse: every recipient is a Bazaar-managed address. Internal traffic stays in GCs.</summary>
    BazaarToBazaar,

    /// <summary>Refuse: no recipient.</summary>
    NoRecipients,

    /// <summary>Refuse: a recipient is not a plain address, or the subject has a line break.</summary>
    InvalidMessage,

    /// <summary>Refuse: the message is larger than the effective byte limit.</summary>
    TooLarge,

    /// <summary>Refuse: more attachments than the effective limit.</summary>
    TooManyAttachments,

    /// <summary>Pause: the rate limiter denied the new-recipient daily cap.</summary>
    NewRecipientCap,

    /// <summary>Pause: the rate limiter denied the install-wide daily send quota (<c>security.quota.mail_sent_per_day</c>).</summary>
    DailyQuota,

    /// <summary>Pause: the rate limiter denied this agent's daily send sub-limit.</summary>
    AgentDailyQuota,
}

/// <summary>
/// The policy's answer. <see cref="Footer"/> and <see cref="IdempotencyKey"/> are set for Send and NeedsOwnerApproval, so the
/// approval card can show the footer preview and a repeated key sends once.
/// </summary>
/// <param name="Kind">What to do.</param>
/// <param name="Reason">The reason code.</param>
/// <param name="Tier">1 for a Tier 1 send, 2 for approval, 0 when no tier applies (refuse, pause).</param>
/// <param name="IdempotencyKey">The derived key, or empty when the message is refused or paused.</param>
/// <param name="Footer">The effective footer, never empty for Send and NeedsOwnerApproval; empty when refused or paused.</param>
/// <param name="RetryAfter">For Pause: when the limiter says a slot frees. Zero otherwise.</param>
public sealed record OutboundDecision(
    OutboundDecisionKind Kind,
    OutboundReason Reason,
    int Tier,
    string IdempotencyKey,
    string Footer,
    TimeSpan RetryAfter = default);

/// <summary>The send policy (doc 22, outbound flow).</summary>
public interface IOutboundMailPolicy
{
    Task<OutboundDecision> EvaluateAsync(OutboundRequest request, CancellationToken ct);
}

/// <summary>Port: is this address managed by this Bazaar install (any agent or owner mailbox)?</summary>
public interface IBazaarAddressDirectory
{
    ValueTask<bool> IsBazaarAddressAsync(string address, CancellationToken ct);
}

/// <summary>Port: the owner's approved recipient list for one agent (doc 15, Tier 1 mapping).</summary>
public interface IApprovedRecipients
{
    ValueTask<bool> IsApprovedAsync(string agentId, string address, CancellationToken ct);
}

/// <summary>
/// Port: the settings this policy reads (doc 22, Settings). Read only. A value that is missing or out of range is treated as the
/// shipped default or tightened, never loosened below a floor.
/// </summary>
public interface IOutboundMailSettings
{
    /// <summary><c>mail.new_recipients_per_agent_day</c>.</summary>
    int NewRecipientsPerAgentDay { get; }

    /// <summary><c>security.quota.mail_sent_per_day</c> (owned by doc 15).</summary>
    int SentPerDay { get; }

    /// <summary>Per-agent sub-limit on sends per day (PROPOSED <c>mail.sent_per_agent_day</c>), enforced on top of <see cref="SentPerDay"/>.</summary>
    int SentPerAgentDay { get; }

    /// <summary><c>mail.footer.operator_contact</c> (PROPOSED). Blank adds no contact line. It never replaces the footer.</summary>
    string? OperatorContact { get; }

    /// <summary><c>mail.footer.wording</c>. Empty or whitespace falls back to the shipped footer.</summary>
    string? FooterWording { get; }

    /// <summary><c>mail.max_message_bytes</c>.</summary>
    long MaxMessageBytes { get; }

    /// <summary><c>mail.max_attachments</c>.</summary>
    int MaxAttachments { get; }

    /// <summary>The back end's hard message cap (25 MiB on Cloudflare). A ceiling no setting may exceed.</summary>
    long BackendHardCapBytes { get; }
}

/// <summary>Shipped defaults for the settings above (doc 22, Settings table).</summary>
public static class OutboundDefaults
{
    public const int NewRecipientsPerAgentDay = 20;

    public const int SentPerDay = 20;

    public const int SentPerAgentDay = 20;

    public const long MaxMessageBytes = 10L * 1024 * 1024;

    public const int MaxAttachments = 5;

    /// <summary>
    /// The shipped footer. Domain-agnostic. A footer is always appended; a blank <c>mail.footer.wording</c> falls back to this
    /// text, never to no footer.
    /// </summary>
    public const string Footer = "This message was written and sent by an AI agent, not a person. Reply to this message to reach the operator who runs this agent.";
}
