namespace Bazaar.Server.MailPolicy;

/// <summary>
/// The v1 mail back ends an operator can choose (doc 22 back ends table, doc 45 step 11). The wire form of the setting is the
/// snake_case name.
/// </summary>
public enum MailBackendKind
{
    /// <summary>No back end chosen. The only value while mail is off.</summary>
    None = 0,

    /// <summary>Built-in Bazaar mail client over Bazaar-managed mailboxes.</summary>
    BazaarClient,

    /// <summary>Generic IMAP/SMTP to an operator-named provider.</summary>
    ImapSmtp,

    /// <summary>Cloudflare Email Routing with an HTTPS sender.</summary>
    Cloudflare,

    /// <summary>Built-in SMTP/IMAP server (VPS only; the inbound exception is doc 22 question 2).</summary>
    BuiltinServer,
}

/// <summary>The names of the operator's mail choice settings (doc 22 settings table; doc 45 step 11). Pure constants for the setup projects.</summary>
public static class MailPolicySettingNames
{
    /// <summary>Whether the operator turned mail on. Off by default (doc 45 section 1.12).</summary>
    public const string Enabled = "mail.enabled";

    /// <summary>The chosen back end (<see cref="MailBackendKind"/>, snake_case wire form).</summary>
    public const string Backend = "mail.backend";

    /// <summary>The snake_case plan and wire ids of the back ends an enabled mail setup may choose, in doc 22 order.</summary>
    public static readonly IReadOnlyList<string> BackendIds = ["bazaar_client", "imap_smtp", "cloudflare", "builtin_server"];
}
