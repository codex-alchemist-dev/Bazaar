using System.Text;
using System.Text.RegularExpressions;
using Bazaar.Server.Mail.Quarantine;
using MimeKit.Text;

namespace Bazaar.Server.Mail.Parsing;

/// <summary>
/// One http or https link found in a message, for the reader session (doc 22 inbound step 5). It carries the normalized URL and
/// flags only: no body text and no display text, so a hand-over built from it cannot carry sender prose.
/// </summary>
/// <param name="Url">Normalized absolute URL: lowercase scheme and host, punycode host, default port dropped, tracking-only fragment removed. The query is kept as written.</param>
/// <param name="Host">The host in its ASCII (punycode) form.</param>
/// <param name="DisplayTextMismatch">True when some anchor showed a host-like text that names a different host than the target.</param>
/// <param name="IsInternationalized">True when the host contains non-ASCII characters or an <c>xn--</c> label.</param>
/// <param name="IsLookalike">True when the host imitates a protected domain (<see cref="LookalikeDomainDetector"/>).</param>
/// <param name="HasUserInfo">True when the URL carries <c>user@</c> before the host, a common disguise.</param>
public sealed record ExtractedLink(
    string Url,
    string Host,
    bool DisplayTextMismatch,
    bool IsInternationalized,
    bool IsLookalike,
    bool HasUserInfo);

/// <summary>The links of one message plus how many were left out by the limits.</summary>
/// <param name="Links">Distinct links in first-seen order (text part first, then HTML), at most <see cref="MailLimits.MaxLinks"/>.</param>
/// <param name="DroppedTooLong">Distinct links longer than <see cref="MailLimits.MaxLinkLength"/> that were not kept.</param>
/// <param name="Truncated">True when more distinct links existed than <see cref="MailLimits.MaxLinks"/>.</param>
public sealed record MailLinkReport(IReadOnlyList<ExtractedLink> Links, int DroppedTooLong, bool Truncated);

/// <summary>
/// Pure, deterministic link extraction from the text and HTML parts of a parsed message. It never fetches or resolves anything and
/// does not rewrite a URL beyond normalization.
/// </summary>
public static partial class MailLinkExtractor
{
    // Display text is only scanned up to this many characters per anchor; a host-like text is far shorter.
    private const int _maxDisplayScan = 512;

    private static readonly string[] _trackingFragmentPrefixes = ["utm_", "fbclid", "gclid", "mc_", "_hsenc", "_hsmi", ":~:text="];

    [GeneratedRegex(@"https?://[^\s<>""]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PlainUrlPattern();

    /// <summary>Extracts the links of <paramref name="mail"/>.</summary>
    /// <param name="mail">The parsed message.</param>
    /// <param name="limits">Count and length caps.</param>
    /// <param name="protectedDomains">Domains whose lookalikes are flagged; supplied by the caller, never built in.</param>
    public static MailLinkReport Extract(ParsedMail mail, MailLimits limits, IEnumerable<string> protectedDomains)
    {
        ArgumentNullException.ThrowIfNull(mail);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(protectedDomains);
        limits.Validate();

        var collector = new Collector(limits, [.. protectedDomains]);

        if (!string.IsNullOrEmpty(mail.TextBody))
        {
            foreach (Match match in PlainUrlPattern().Matches(mail.TextBody))
            {
                if (!collector.Add(TrimTrailingPunctuation(match.Value), displayText: null))
                {
                    break;
                }
            }
        }

        if (!collector.Truncated && !string.IsNullOrEmpty(mail.HtmlBody))
        {
            ScanHtml(mail.HtmlBody, collector);
        }

        return collector.Build();
    }

    private static void ScanHtml(string html, Collector collector)
    {
        using var reader = new StringReader(html);
        var tokenizer = new HtmlTokenizer(reader);
        string? href = null;
        StringBuilder? display = null;

        while (tokenizer.ReadNextToken(out var token))
        {
            switch (token)
            {
                case HtmlTagToken { Id: HtmlTagId.A, IsEndTag: false } tag:
                    // A new anchor closes an unterminated previous one.
                    if (href is not null && !collector.Add(href, display?.ToString()))
                    {
                        return;
                    }

                    href = null;
                    display = null;
                    foreach (var attribute in tag.Attributes)
                    {
                        if (attribute.Id == HtmlAttributeId.Href && attribute.Value is { } value)
                        {
                            href = value.Trim();
                            display = new StringBuilder();
                        }
                    }

                    break;

                case HtmlTagToken { Id: HtmlTagId.A, IsEndTag: true } when href is not null:
                    if (!collector.Add(href, display?.ToString()))
                    {
                        return;
                    }

                    href = null;
                    display = null;
                    break;

                case HtmlDataToken data when display is { Length: < _maxDisplayScan }:
                    display.Append(data.Data);
                    break;
            }
        }

        if (href is not null)
        {
            collector.Add(href, display?.ToString());
        }
    }

    private static string TrimTrailingPunctuation(string url) => url.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '\'');

    /// <summary>Normalizes one candidate, or returns null when it is not an absolute http or https URL with a host.</summary>
    private static Uri? Normalize(string candidate, out string normalized)
    {
        normalized = string.Empty;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        var text = uri.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.Fragment, UriFormat.UriEscaped);
        var fragment = uri.Fragment.TrimStart('#');
        if (fragment.Length > 0 && !IsTrackingFragment(fragment))
        {
            text += uri.Fragment;
        }

        normalized = text;
        return uri;
    }

    private static bool IsTrackingFragment(string fragment)
    {
        var decoded = Uri.UnescapeDataString(fragment);
        return _trackingFragmentPrefixes.Any(prefix => decoded.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsIdn(string host) =>
        host.Any(c => c > 127) || host.Split('.').Any(label => label.StartsWith("xn--", StringComparison.OrdinalIgnoreCase));

    /// <summary>The host a display text names, or null when the text is prose or has no dotted host.</summary>
    private static string? DisplayHost(string? text)
    {
        var candidate = text?.Trim();
        if (string.IsNullOrEmpty(candidate) || candidate.Any(char.IsWhiteSpace))
        {
            return null;
        }

        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = "http://" + candidate;
        }

        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Host.Contains('.') ? uri.IdnHost.ToLowerInvariant() : null;
    }

    private sealed class Collector(MailLimits limits, List<string> protectedDomains)
    {
        private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
        private readonly List<ExtractedLink> _links = [];
        private readonly HashSet<string> _tooLong = new(StringComparer.Ordinal);

        /// <summary>True once a link beyond the cap was seen; callers stop scanning.</summary>
        public bool Truncated { get; private set; }

        /// <summary>Adds one candidate. Returns false when scanning should stop.</summary>
        public bool Add(string candidate, string? displayText)
        {
            var uri = Normalize(candidate, out var normalized);
            if (uri is null)
            {
                return true;
            }

            var mismatch = DisplayHost(displayText) is { } shown
                && !string.Equals(shown, uri.IdnHost, StringComparison.OrdinalIgnoreCase);

            if (_index.TryGetValue(normalized, out var existing))
            {
                if (mismatch && !_links[existing].DisplayTextMismatch)
                {
                    _links[existing] = _links[existing] with { DisplayTextMismatch = true };
                }

                return true;
            }

            if (normalized.Length > limits.MaxLinkLength)
            {
                _tooLong.Add(normalized);
                return true;
            }

            if (_links.Count >= limits.MaxLinks)
            {
                Truncated = true;
                return false;
            }

            var host = uri.IdnHost.ToLowerInvariant();
            _index[normalized] = _links.Count;
            _links.Add(new ExtractedLink(
                normalized,
                host,
                mismatch,
                IsIdn(uri.Host) || IsIdn(host),
                LookalikeDomainDetector.IsLookalike(host, protectedDomains),
                !string.IsNullOrEmpty(uri.UserInfo)));
            return true;
        }

        public MailLinkReport Build() => new([.. _links], _tooLong.Count, Truncated);
    }
}
