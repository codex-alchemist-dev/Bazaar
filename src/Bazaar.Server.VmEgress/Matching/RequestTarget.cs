using System.Globalization;

namespace Bazaar.Server.VmEgress.Matching;

/// <summary>Parses the target of a proxy request into a host and port. Strict: anything ambiguous is refused.</summary>
public static class RequestTarget
{
    /// <summary>
    /// Parses <c>host:port</c> (CONNECT) or the authority of an absolute URI. Userinfo (<c>user@host</c>), paths, spaces and
    /// backslashes are refused, so the host the allowlist sees is the host the proxy connects to.
    /// </summary>
    public static bool TryParseAuthority(string? authority, int defaultPort, out HostName host, out int port)
    {
        host = null!;
        port = defaultPort;
        if (string.IsNullOrEmpty(authority) || authority.Length > 300)
        {
            return false;
        }

        string hostText;
        string? portText = null;
        if (authority[0] == '[')
        {
            var close = authority.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                return false;
            }

            hostText = authority[..(close + 1)];
            var rest = authority[(close + 1)..];
            if (rest.Length > 0)
            {
                if (rest[0] != ':')
                {
                    return false;
                }

                portText = rest[1..];
            }
        }
        else
        {
            var colon = authority.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0)
            {
                if (colon != authority.LastIndexOf(':'))
                {
                    return false;
                }

                hostText = authority[..colon];
                portText = authority[(colon + 1)..];
            }
            else
            {
                hostText = authority;
            }
        }

        if (portText is not null)
        {
            if (portText.Length is 0 or > 5 || portText.AsSpan().ContainsAnyExceptInRange('0', '9')
                || !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
            {
                return false;
            }
        }

        return HostName.TryNormalize(hostText, out host);
    }

    /// <summary>
    /// Splits an absolute-form <c>http://authority/path?query</c> target into its authority parts and origin-form path. Only
    /// <c>http</c> is accepted here; <c>https</c> must come through CONNECT.
    /// </summary>
    public static bool TryParseAbsoluteHttp(string? target, out HostName host, out int port, out string pathAndQuery)
    {
        host = null!;
        port = 80;
        pathAndQuery = "/";
        const string scheme = "http://";
        if (target is null || !target.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = target[scheme.Length..];
        var end = rest.AsSpan().IndexOfAny('/', '?', '#');
        var authority = end < 0 ? rest : rest[..end];
        if (end >= 0)
        {
            var tail = rest[end..];
            if (tail.Contains('#', StringComparison.Ordinal) || tail.Any(c => c <= 0x20 || c == 0x7f))
            {
                return false;
            }

            pathAndQuery = tail[0] == '/' ? tail : "/" + tail;
        }

        return TryParseAuthority(authority, 80, out host, out port);
    }
}
