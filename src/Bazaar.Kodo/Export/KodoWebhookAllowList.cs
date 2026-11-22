using System.Net;

namespace Bazaar.Kodo.Export;

/// <summary>The verdict on one destination. <paramref name="RedactedUrl"/> is the only form of the URL a caller may log.</summary>
/// <param name="Allowed">True when the destination may be called.</param>
/// <param name="Reason">A fixed code: <c>allowed</c>, or the rule that refused it.</param>
/// <param name="RedactedUrl">The URL with its path and query removed (<see cref="KodoWebhookUrl.Redact"/>).</param>
public sealed record KodoWebhookDestinationDecision(bool Allowed, string Reason, string RedactedUrl);

/// <summary>
/// The operator's allow list of webhook destinations (doc 46 section 15.3). An entry is a DNS name (<c>hooks.example.com</c>) or a
/// wildcard over subdomains (<c>*.example.com</c>, which matches <c>a.example.com</c> but not <c>example.com</c>). Every destination must
/// be HTTPS on the default port, with no user info and no address literal. An empty list allows nothing.
/// </summary>
/// <remarks>
/// The rules beyond the name match (HTTPS only, port 443, no address literals) are PROPOSED (doc 46 section 15.7).
/// </remarks>
public sealed class KodoWebhookAllowList
{
    private readonly HashSet<string> _exact;
    private readonly string[] _wildcardSuffixes;

    /// <summary>Builds the list from the operator's entries.</summary>
    /// <param name="hostPatterns">The entries. Case does not matter. Each must be a DNS name or <c>*.</c> over one.</param>
    /// <exception cref="ArgumentException">An entry carries a scheme, port, path, address or any other non-name text.</exception>
    public KodoWebhookAllowList(IEnumerable<string> hostPatterns)
    {
        ArgumentNullException.ThrowIfNull(hostPatterns);

        _exact = new HashSet<string>(StringComparer.Ordinal);
        List<string> suffixes = [];
        foreach (string entry in hostPatterns)
        {
            ArgumentNullException.ThrowIfNull(entry);
            string normalized = entry.Trim().ToLowerInvariant();
            if (!KodoExportPatterns.HostPattern().IsMatch(normalized) || IPAddress.TryParse(normalized, out _))
            {
                throw new ArgumentException(
                    "An allow-list entry must be a DNS name or *. followed by one, with no scheme, port, path or address.",
                    nameof(hostPatterns));
            }

            if (normalized.StartsWith("*.", StringComparison.Ordinal))
            {
                suffixes.Add(normalized[1..]);
            }
            else
            {
                _exact.Add(normalized);
            }
        }

        _wildcardSuffixes = [.. suffixes];
    }

    /// <summary>The list with no entries. It allows no destination.</summary>
    public static KodoWebhookAllowList Empty { get; } = new([]);

    /// <summary>Decides whether a destination may be called.</summary>
    /// <param name="url">The destination URL as configured.</param>
    public KodoWebhookDestinationDecision Evaluate(string? url)
    {
        string redacted = KodoWebhookUrl.Redact(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || string.IsNullOrEmpty(uri.Host))
        {
            return new KodoWebhookDestinationDecision(false, "not_absolute_url", redacted);
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return new KodoWebhookDestinationDecision(false, "scheme_not_https", redacted);
        }

        if (uri.UserInfo.Length > 0)
        {
            return new KodoWebhookDestinationDecision(false, "userinfo_not_allowed", redacted);
        }

        if (uri.Port != 443)
        {
            return new KodoWebhookDestinationDecision(false, "port_not_allowed", redacted);
        }

        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            return new KodoWebhookDestinationDecision(false, "ip_literal_not_allowed", redacted);
        }

        string host = uri.Host.ToLowerInvariant();
        if (_exact.Contains(host))
        {
            return new KodoWebhookDestinationDecision(true, "allowed", redacted);
        }

        foreach (string suffix in _wildcardSuffixes)
        {
            if (host.Length > suffix.Length && host.EndsWith(suffix, StringComparison.Ordinal))
            {
                return new KodoWebhookDestinationDecision(true, "allowed", redacted);
            }
        }

        return new KodoWebhookDestinationDecision(false, "not_on_allow_list", redacted);
    }
}
