namespace Bazaar.Client.Mail.Abstractions;

// The mail pane models (doc 22, WP12). The server derives them and sends them as pane JSON. These records are the typed
// shape the client reads them into. They reference no server project: the DNS enums use the same names as the server's
// MailDnsReport, so a report maps one to one without a reference.

/// <summary>Held-mail states from doc 22 (quarantine). A released message is still untrusted.</summary>
public enum MailQuarantineState
{
    Held,
    Released,
    Purged,
    Reported,
}

/// <summary>One quarantined message, as metadata only. The raw content is not in the model (doc 22).</summary>
/// <param name="SenderDomain">The sender's domain, as the server parsed it. Untrusted text.</param>
/// <param name="ReasonCodes">Why the message was held. Untrusted text.</param>
/// <param name="AuthResult">The sender authentication result, as text.</param>
/// <param name="ReceivedAt">When the message arrived.</param>
/// <param name="State">The held-mail state.</param>
public sealed record MailQuarantineRow(
    string SenderDomain,
    IReadOnlyList<string> ReasonCodes,
    string AuthResult,
    DateTimeOffset ReceivedAt,
    MailQuarantineState State);

/// <summary>One sender domain in the daily digest.</summary>
public sealed record MailSenderDomainCount(string Domain, int Count);

/// <summary>The owner's daily quarantine digest: counts and top sender domains, never message bodies (doc 22).</summary>
public sealed record MailQuarantineDigest(
    int Held,
    int Released,
    int Purged,
    int Reported,
    IReadOnlyList<MailSenderDomainCount> TopSenderDomains);

/// <summary>One Bazaar-managed inbound message. Text is plain text; it is never rendered as markup (doc 22).</summary>
/// <param name="From">The sender as the server shows it. Untrusted text.</param>
/// <param name="Subject">The subject. Untrusted text.</param>
/// <param name="ReceivedAt">When the message arrived.</param>
/// <param name="Text">The plain-text body. Untrusted text.</param>
/// <param name="Links">The URLs found in the message. The client shows each by its host and never opens or fetches it.</param>
public sealed record MailboxMessageRow(
    string From,
    string Subject,
    DateTimeOffset ReceivedAt,
    string Text,
    IReadOnlyList<string> Links);

/// <summary>
/// The card for a hard-tainted outbound send that needs the owner (Tier 2, doc 22). The client only shows it. Release and
/// send decisions go through Bazaar.Approvals.
/// </summary>
/// <param name="From">The From address, set by the server. The agent cannot choose it.</param>
/// <param name="Recipients">The recipient addresses.</param>
/// <param name="Subject">The subject.</param>
/// <param name="Body">The plain-text body.</param>
/// <param name="FooterPreview">The AI-agent footer the server appends (doc 22, outbound step 3).</param>
/// <param name="Reason">Why the send needs approval.</param>
public sealed record MailApprovalCard(
    string From,
    IReadOnlyList<string> Recipients,
    string Subject,
    string Body,
    string FooterPreview,
    string Reason);

/// <summary>The DNS checks the server runs for an operator's domain (doc 22, WP10). Names match the server enum.</summary>
public enum MailDnsCheckKind
{
    Spf,
    Dkim,
    Dmarc,
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Naming",
        "CA1720:Identifier contains type name",
        Justification = "The name is the server's MailDnsCheckKind.Ptr, which the 1:1 report mapping needs (doc 22, WP10).")]
    Ptr,
    BulkSender,
}

/// <summary>The outcome of one DNS check. Names match the server enum.</summary>
public enum MailDnsStatus
{
    Pass,
    Warn,
    Fail,
    NotApplicable,
}

/// <summary>One DNS report row: a check, its status, a plain-words detail and an optional fix hint.</summary>
/// <param name="Kind">The check.</param>
/// <param name="Status">The check's status.</param>
/// <param name="Detail">What the check found, in plain words. Untrusted text.</param>
/// <param name="Fix">What the operator should change, or null when nothing needs changing. Untrusted text.</param>
public sealed record MailDnsRow(MailDnsCheckKind Kind, MailDnsStatus Status, string Detail, string? Fix);
