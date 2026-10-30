namespace Bazaar.Server.Mail.Parsing;

/// <summary>
/// Parse limits (doc 22 inbound step 2). Shipped defaults mirror the settings registry rows; the settings layer may
/// tighten them but never raise them above the back end's hard cap (25 MiB on Cloudflare, verified).
/// </summary>
public sealed record MailLimits
{
    /// <summary><c>mail.max_message_bytes</c>, default 10 MiB.</summary>
    public long MaxMessageBytes { get; init; } = 10 * 1024 * 1024;

    /// <summary><c>mail.max_attachments</c>, default 5.</summary>
    public int MaxAttachments { get; init; } = 5;

    /// <summary>Nesting depth of MIME parts. Not a settings row yet; fixed here.</summary>
    public int MaxDepth { get; init; } = 8;

    /// <summary>Total MIME parts in one message. Not a settings row yet; fixed here.</summary>
    public int MaxParts { get; init; } = 64;

    public static MailLimits Defaults { get; } = new();

    public void Validate()
    {
        if (MaxMessageBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaxMessageBytes));
        if (MaxAttachments < 0) throw new ArgumentOutOfRangeException(nameof(MaxAttachments));
        if (MaxDepth <= 0) throw new ArgumentOutOfRangeException(nameof(MaxDepth));
        if (MaxParts <= 0) throw new ArgumentOutOfRangeException(nameof(MaxParts));
    }
}
