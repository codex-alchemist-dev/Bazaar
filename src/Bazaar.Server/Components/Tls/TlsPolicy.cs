using System.Net;

namespace Bazaar.Server.Components.Tls;

/// <summary>
/// Where plain <c>ws://</c> is allowed (docs/design/32 section 2). Plain text only on loopback (127.0.0.0/8 and ::1). Any
/// other bind address needs TLS. Pure: no DNS lookups, so a host name is never resolved and "localhost" is not loopback here.
/// </summary>
public static class TlsPolicy
{
    /// <summary>True only when the address is loopback (127.0.0.0/8 or ::1). Null is not loopback.</summary>
    public static bool IsPlainTextAllowed(IPAddress? address) =>
        address is not null && IPAddress.IsLoopback(address);

    /// <summary>
    /// True only when the text is a loopback IP literal, for example <c>127.0.0.1</c> or <c>[::1]</c>. Host names are not
    /// resolved, so any other text, including "localhost", returns false.
    /// </summary>
    public static bool IsPlainTextAllowedForHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        var literal = host.Trim();
        if (literal.StartsWith('[') && literal.EndsWith(']'))
            literal = literal[1..^1];

        return IPAddress.TryParse(literal, out var address) && IsPlainTextAllowed(address);
    }
}
