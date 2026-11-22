using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Bazaar.Server.BrowserPool.Abstractions;

namespace Bazaar.Server.BrowserPool;

/// <summary>
/// Pure checks on what a caller or a policy hands the pool: the request, and the egress endpoint. Anything that reaches an argv
/// list is checked here first, and <see cref="PoolCommandLine"/> checks it again.
/// </summary>
public static partial class PoolRequestValidator
{
    /// <summary>Longest start URL accepted.</summary>
    public const int MaxUrlLength = 2048;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex AgentIdPattern();

    /// <summary>True for an id that is safe to use in audit rows and as a map key. It never reaches an argv list.</summary>
    public static bool IsValidAgentId(string? agentId) => agentId is not null && AgentIdPattern().IsMatch(agentId);

    /// <summary>
    /// Checks a request. Returns null when valid, with the exact string to pass to the browser (<c>about:blank</c>, or the URL
    /// after <see cref="Uri"/> normalisation, never the caller's raw text). Otherwise returns the reason.
    /// </summary>
    public static string? Validate(PoolSessionRequest? request, out string startUrl)
    {
        startUrl = "about:blank";
        if (request is null)
        {
            return "The request is missing.";
        }

        if (!IsValidAgentId(request.AgentId))
        {
            return "AgentId must be 1 to 64 letters, digits, '_' or '-', starting with a letter or digit.";
        }

        if (!Enum.IsDefined(request.Display))
        {
            return "Display is not a known mode.";
        }

        if (request.Width is < 320 or > 3840 || request.Height is < 240 or > 2160)
        {
            return "Width must be 320 to 3840 and Height 240 to 2160.";
        }

        if (request.StartUrl is null)
        {
            return null;
        }

        var urlError = ValidateUrl(request.StartUrl, out var normalised);
        if (urlError is not null)
        {
            return urlError;
        }

        startUrl = normalised;
        return null;
    }

    /// <summary>Checks a start URL: absolute http or https, at most 2048 characters, no control characters, no user info. Normalised through <see cref="Uri"/>.</summary>
    public static string? ValidateUrl(string url, out string normalised)
    {
        normalised = string.Empty;
        if (url.Length is 0 or > MaxUrlLength)
        {
            return "StartUrl must be 1 to 2048 characters.";
        }

        foreach (var c in url)
        {
            if (char.IsControl(c))
            {
                return "StartUrl may not contain control characters.";
            }
        }

        if (url[0] == '-')
        {
            return "StartUrl may not start with '-'.";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "StartUrl must be an absolute http or https URL.";
        }

        if (uri.UserInfo.Length != 0)
        {
            return "StartUrl may not carry user info.";
        }

        if (uri.Host.Length == 0)
        {
            return "StartUrl needs a host.";
        }

        var absolute = uri.AbsoluteUri;
        if (absolute.Length == 0 || absolute[0] == '-' || absolute.Length > MaxUrlLength * 3)
        {
            return "StartUrl is not acceptable.";
        }

        normalised = absolute;
        return null;
    }

    /// <summary>True only for a canonical dotted-quad IPv4 address (<c>10.0.0.5</c>, not <c>10.1</c>, not a name, not IPv6).</summary>
    public static bool IsIpv4Literal(string? host) =>
        host is not null
        && IPAddress.TryParse(host, out var ip)
        && ip.AddressFamily == AddressFamily.InterNetwork
        && ip.ToString() == host;

    /// <summary>
    /// Checks an egress endpoint: an IPv4 literal that is not unspecified, broadcast or multicast, and a port in 1 to 65535.
    /// Loopback is allowed because a test or a local relay may use it; the firewall drops every other loopback use.
    /// </summary>
    public static string? ValidateEndpoint(PoolEgressEndpoint? endpoint)
    {
        if (endpoint is null)
        {
            return "No egress endpoint.";
        }

        if (!IsIpv4Literal(endpoint.Host))
        {
            return "The egress proxy host must be an IPv4 address literal.";
        }

        var first = IPAddress.Parse(endpoint.Host).GetAddressBytes()[0];
        if (first == 0 || first >= 224)
        {
            return "The egress proxy address is unspecified, multicast or broadcast.";
        }

        if (endpoint.Port is < 1 or > 65535)
        {
            return "The egress proxy port must be 1 to 65535.";
        }

        return null;
    }

    internal static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
