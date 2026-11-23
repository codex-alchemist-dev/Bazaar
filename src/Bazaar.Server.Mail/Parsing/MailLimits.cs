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

    /// <summary>Most entries an attached archive may list before it is held. PROPOSED key <c>mail.max_archive_entries</c>.</summary>
    public int MaxArchiveEntries { get; init; } = 100;

    /// <summary>Most bytes an attached archive may declare once unpacked. PROPOSED key <c>mail.max_archive_uncompressed_bytes</c>.</summary>
    public long MaxArchiveUncompressedBytes { get; init; } = 100L * 1024 * 1024;

    /// <summary>Highest declared uncompressed-to-compressed ratio. PROPOSED key <c>mail.max_archive_ratio</c>.</summary>
    public int MaxArchiveCompressionRatio { get; init; } = 100;

    /// <summary>Most distinct links kept per message for the reader session. PROPOSED key <c>mail.max_links</c>.</summary>
    public int MaxLinks { get; init; } = 50;

    /// <summary>Longest normalized link kept, in UTF-16 code units; longer ones are counted and dropped. PROPOSED key <c>mail.max_link_length</c>.</summary>
    public int MaxLinkLength { get; init; } = 2048;

    public static MailLimits Defaults { get; } = new();

    public void Validate()
    {
        if (MaxMessageBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaxMessageBytes));
        if (MaxAttachments < 0) throw new ArgumentOutOfRangeException(nameof(MaxAttachments));
        if (MaxDepth <= 0) throw new ArgumentOutOfRangeException(nameof(MaxDepth));
        if (MaxParts <= 0) throw new ArgumentOutOfRangeException(nameof(MaxParts));
        if (MaxArchiveEntries <= 0) throw new ArgumentOutOfRangeException(nameof(MaxArchiveEntries));
        if (MaxArchiveUncompressedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaxArchiveUncompressedBytes));
        if (MaxArchiveCompressionRatio <= 0) throw new ArgumentOutOfRangeException(nameof(MaxArchiveCompressionRatio));
        if (MaxLinks < 0) throw new ArgumentOutOfRangeException(nameof(MaxLinks));
        if (MaxLinkLength <= 0) throw new ArgumentOutOfRangeException(nameof(MaxLinkLength));
    }
}
