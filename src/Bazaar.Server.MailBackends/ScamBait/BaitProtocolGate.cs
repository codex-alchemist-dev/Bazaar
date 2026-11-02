using Bazaar.Server.Mail;

namespace Bazaar.Server.MailBackends.ScamBait;

/// <summary>
/// Gate for the optional scam-baiting protocol (doc 22, "Scam-baiting protocol"). Off by default
/// (setting security.scam_mail.protocol_enabled, false). This class only decides whether one reply may be
/// sent. It writes no message content and sends nothing.
/// </summary>
public static class BaitProtocolGate
{
    /// <summary>Signals in an inbound thread that end the protocol at once and disable it.</summary>
    public enum StopSignal
    {
        RealThirdPartyDetected,
        Threat,
        CredentialRequest,
        MoneyRequest,
    }

    public enum Verdict
    {
        Allowed,
        Disabled,
        NotAcknowledged,
        PersonaRequired,
        AgentMailboxForbidden,
        StopTriggered,
        OwnerApprovalRequired,
    }

    /// <summary>The protocol's install-level state. Enabling it records a dated acknowledgement in the audit log.</summary>
    public sealed record BaitSettings(bool ProtocolEnabled, DateTimeOffset? AcknowledgedAt)
    {
        public static BaitSettings Default { get; } = new(ProtocolEnabled: false, AcknowledgedAt: null);
    }

    /// <summary>One reply the protocol wants to send. The sender must be a persona mailbox the caller marks as such.</summary>
    public sealed record ReplyRequest(
        MailboxRef From,
        bool IsPersonaMailbox,
        bool IsAgentMailbox,
        bool OwnerApproved,
        string? OwnerReason,
        IReadOnlySet<StopSignal> Signals);

    /// <summary>Decides one reply. Checks run in a fixed order so the first failing rule is the reported reason.</summary>
    public static Verdict Evaluate(BaitSettings settings, ReplyRequest request)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(request);

        if (!settings.ProtocolEnabled) return Verdict.Disabled;
        if (settings.AcknowledgedAt is null) return Verdict.NotAcknowledged;

        // The bait identity never uses an agent's real address (doc 22).
        if (request.IsAgentMailbox) return Verdict.AgentMailboxForbidden;
        if (!request.IsPersonaMailbox) return Verdict.PersonaRequired;

        // A stop signal wins over any approval. Real people, threats, credentials and money end the thread.
        if (request.Signals.Count > 0) return Verdict.StopTriggered;

        // Every reply is owner-approved with a written reason; approvals never carry over.
        if (!request.OwnerApproved || string.IsNullOrWhiteSpace(request.OwnerReason)) return Verdict.OwnerApprovalRequired;

        return Verdict.Allowed;
    }

    /// <summary>True when the verdict means the protocol must switch off, not just skip this reply.</summary>
    public static bool MustDisable(Verdict verdict) => verdict == Verdict.StopTriggered;
}
