namespace Bazaar.Server.Email.Quarantine;

/// <summary>
/// Why an inbound message was held. Values are in the order decisions report them, so reasons sort the same way every time.
/// </summary>
public enum QuarantineReason
{
    /// <summary>SPF, DKIM or DMARC reported a failure.</summary>
    AuthenticationFailed,

    /// <summary>The display name shows an address that is not the one the message came from.</summary>
    DisplayNameMismatch,

    /// <summary>An attachment is an executable or script.</summary>
    ExecutableAttachment,

    /// <summary>An attachment is a macro-enabled Office file.</summary>
    MacroAttachment,

    /// <summary>A link's visible text names a host that differs from the link's target.</summary>
    LinkTextMismatch,

    /// <summary>The message is larger than the configured limit.</summary>
    OversizeMessage,

    /// <summary>The message has more attachments than the configured limit.</summary>
    TooManyAttachments,
}
