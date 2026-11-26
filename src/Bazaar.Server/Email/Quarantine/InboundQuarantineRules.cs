using System.Collections.Immutable;

namespace Bazaar.Server.Email.Quarantine;

/// <summary>One attachment as parsed from a message.</summary>
/// <param name="FileName">The attachment's file name as sent.</param>
/// <param name="ContentType">The declared media type, or null when none was given. Not used by the screens yet.</param>
public sealed record AttachmentInfo(string FileName, string? ContentType);

/// <summary>A link in a message body: the text the reader sees and the URL it points to.</summary>
/// <param name="VisibleText">The link's visible text.</param>
/// <param name="Target">The link's target as written in the message.</param>
public sealed record LinkInfo(string VisibleText, string Target);

/// <summary>
/// What the screens need from one parsed inbound message: the sender, the authentication results, the size, and the
/// attachments and links. The caller parses MIME; this record holds only the results, so the rules stay pure.
/// </summary>
/// <param name="From">The From header as sent, or null when missing.</param>
/// <param name="SpfResult">The SPF result text (for example pass, fail, none), or null when not recorded.</param>
/// <param name="DkimResult">The DKIM result text, or null when not recorded.</param>
/// <param name="DmarcResult">The DMARC result text, or null when not recorded.</param>
/// <param name="MessageBytes">The raw message size in bytes.</param>
/// <param name="Attachments">Every attachment in the message.</param>
/// <param name="Links">Every link in the message body.</param>
public sealed record InboundHeaders(
    string? From,
    string? SpfResult,
    string? DkimResult,
    string? DmarcResult,
    long MessageBytes,
    IReadOnlyList<AttachmentInfo> Attachments,
    IReadOnlyList<LinkInfo> Links);

/// <summary>
/// The thresholds for the size and attachment screens. Defaults are the shipped values of doc 22's settings
/// (<c>mail.max_message_bytes</c> and <c>mail.max_attachments</c>). Operators may tighten them through settings.
/// </summary>
public sealed record InboundQuarantineOptions
{
    /// <summary>The largest message that is screened as normal (<c>mail.max_message_bytes</c>, 10 MiB).</summary>
    public long MaxMessageBytes { get; init; } = 10_485_760L;

    /// <summary>The most attachments a message may carry (<c>mail.max_attachments</c>).</summary>
    public int MaxAttachments { get; init; } = 5;
}

/// <summary>
/// Pure quarantine screens for inbound mail (doc 22, inbound flow step 4). Takes the parsed results and returns Deliver
/// or Quarantine with every reason that applied. No SMTP, IMAP, DNS or other I/O, and no state.
/// </summary>
public static class InboundQuarantineRules
{
    private static readonly ImmutableHashSet<string> _executableExtensions = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "apk", "application", "bat", "cmd", "com", "cpl", "dll", "exe", "gadget", "hta", "jar", "js", "jse",
        "lnk", "msc", "msi", "msp", "pif", "ps1", "psm1", "reg", "scf", "scr", "sh", "vb", "vbe", "vbs", "wsf", "wsh");

    private static readonly ImmutableHashSet<string> _macroExtensions = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "docm", "dotm", "potm", "ppam", "ppsm", "pptm", "sldm", "xlam", "xlsm", "xltm");

    // Characters that end a token when looking for addresses inside a display name.
    private static readonly char[] _displayTokenSeparators =
        [' ', '\t', '<', '>', '(', ')', '[', ']', '"', ',', ';', ':', '\''];

    /// <summary>Screens one inbound message.</summary>
    /// <param name="headers">The parsed message.</param>
    /// <param name="options">The thresholds. Defaults match doc 22.</param>
    /// <returns>Quarantine with every reason, in enum order; or Deliver with no reasons.</returns>
    public static QuarantineDecision Evaluate(InboundHeaders headers, InboundQuarantineOptions options)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxMessageBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxAttachments);
        ArgumentOutOfRangeException.ThrowIfNegative(headers.MessageBytes);
        ArgumentNullException.ThrowIfNull(headers.Attachments);
        ArgumentNullException.ThrowIfNull(headers.Links);

        var reasons = new SortedSet<QuarantineReason>();

        if (IsFailure(headers.SpfResult) || IsFailure(headers.DkimResult) || IsFailure(headers.DmarcResult))
        {
            reasons.Add(QuarantineReason.AuthenticationFailed);
        }

        if (DisplayNameImpersonates(headers.From))
        {
            reasons.Add(QuarantineReason.DisplayNameMismatch);
        }

        foreach (var attachment in headers.Attachments)
        {
            var extension = ExtensionOf(attachment.FileName);
            if (extension is null)
            {
                continue;
            }

            if (_executableExtensions.Contains(extension))
            {
                reasons.Add(QuarantineReason.ExecutableAttachment);
            }

            if (_macroExtensions.Contains(extension))
            {
                reasons.Add(QuarantineReason.MacroAttachment);
            }
        }

        if (headers.Links.Any(LinkMismatches))
        {
            reasons.Add(QuarantineReason.LinkTextMismatch);
        }

        if (headers.MessageBytes > options.MaxMessageBytes)
        {
            reasons.Add(QuarantineReason.OversizeMessage);
        }

        if (headers.Attachments.Count > options.MaxAttachments)
        {
            reasons.Add(QuarantineReason.TooManyAttachments);
        }

        if (reasons.Count == 0)
        {
            return new QuarantineDecision(QuarantineAction.Deliver, []);
        }

        return new QuarantineDecision(QuarantineAction.Quarantine, [.. reasons]);
    }

    /// <summary>SPF, DKIM and DMARC report "fail" as a failure. Other results (none, neutral, temperror) are not screened here.</summary>
    private static bool IsFailure(string? result) =>
        string.Equals(result?.Trim(), "fail", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the display name contains an address other than the one the message came from.</summary>
    private static bool DisplayNameImpersonates(string? from)
    {
        if (!EmailAddressParser.TryParseMailbox(from, out var mailbox) || mailbox!.DisplayName is null)
        {
            return false;
        }

        var actual = mailbox.Address.ComparisonKey;
        foreach (var token in mailbox.DisplayName.Split(_displayTokenSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Contains('@')
                && EmailAddressParser.TryParse(token, out var shown)
                && shown!.ComparisonKey != actual)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the visible text names a host and that host differs from the target's host. Text with spaces, or
    /// without a dot in its host, is treated as prose, not as a host.
    /// </summary>
    private static bool LinkMismatches(LinkInfo link)
    {
        var shown = HostOf(link.VisibleText, requireDot: true);
        var target = HostOf(link.Target, requireDot: false);
        return shown is not null && target is not null && !string.Equals(shown, target, StringComparison.OrdinalIgnoreCase);
    }

    private static string? HostOf(string text, bool requireDot)
    {
        var candidate = text.Trim();
        if (candidate.Length == 0 || (requireDot && candidate.Any(char.IsWhiteSpace)))
        {
            return null;
        }

        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = "http://" + candidate;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        if (requireDot && !uri.Host.Contains('.'))
        {
            return null;
        }

        return uri.Host;
    }

    /// <summary>The lowercased extension of a file name. Trailing dots and spaces are ignored, so "a.exe." counts as .exe.</summary>
    private static string? ExtensionOf(string fileName)
    {
        var name = fileName.Trim().TrimEnd('.', ' ');
        var dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1)
        {
            return null;
        }

        return name[(dot + 1)..].ToLowerInvariant();
    }
}
