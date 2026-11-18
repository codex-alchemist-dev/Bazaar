using System.Globalization;
using System.Net;

namespace Bazaar.Server.VmEgress.Matching;

/// <summary>
/// A host name in the one form the allowlist compares: lower case ASCII (IDNA punycode), no trailing dot, or a canonical IP literal.
/// Anything that does not parse strictly is refused, never guessed at.
/// </summary>
/// <param name="Name">The normalised name. For an IP literal, its canonical text.</param>
/// <param name="Address">Set when the host is an IP literal.</param>
public sealed record HostName(string Name, IPAddress? Address)
{
    private const int MaxLabel = 63;
    private const int MaxName = 253;

    private static readonly IdnMapping _idn = new() { UseStd3AsciiRules = true, AllowUnassigned = false };

    /// <summary>True when the host is an IP literal.</summary>
    public bool IsIpLiteral => Address is not null;

    /// <summary>
    /// Normalises a host as written in a request or an allowlist entry. Returns false for anything unsafe or odd: userinfo,
    /// ports, paths, spaces, empty or oversized labels, invalid punycode, and numeric forms such as <c>2130706433</c>,
    /// <c>0x7f.1</c> or <c>127.1</c> that some clients read as an address.
    /// </summary>
    public static bool TryNormalize(string? host, out HostName result)
    {
        result = null!;
        if (string.IsNullOrEmpty(host) || host.Length > MaxName + 2)
        {
            return false;
        }

        if (host[0] == '[')
        {
            // Bracketed IPv6 literal. Zone ids are refused: they are host-local and make no sense for a target.
            if (host[^1] != ']' || host.Contains('%', StringComparison.Ordinal))
            {
                return false;
            }

            if (!IPAddress.TryParse(host.AsSpan(1, host.Length - 2), out var v6) || v6.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                return false;
            }

            result = new HostName(v6.ToString(), v6);
            return true;
        }

        // One trailing dot is the absolute form of the same name. A second one is an empty label and fails below.
        var text = host.EndsWith('.') ? host[..^1] : host;
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (c <= 0x20 || c == 0x7f || c is '@' or '/' or '\\' or ':' or '?' or '#' or '[' or ']' or '%' or '*' or '_' or '"' or '\'')
            {
                return false;
            }
        }

        string ascii;
        if (text.All(char.IsAscii))
        {
            ascii = text.ToLowerInvariant();
        }
        else
        {
            try
            {
                ascii = _idn.GetAscii(text).ToLowerInvariant();
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        if (ascii.Length > MaxName)
        {
            return false;
        }

        var labels = ascii.Split('.');
        foreach (var label in labels)
        {
            if (label.Length is 0 or > MaxLabel || label[0] == '-' || label[^1] == '-')
            {
                return false;
            }

            foreach (var c in label)
            {
                if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
                {
                    return false;
                }
            }

            if (label.StartsWith("xn--", StringComparison.Ordinal) && !IsValidPunycode(label))
            {
                return false;
            }
        }

        var last = labels[^1];
        if (last.All(char.IsAsciiDigit) || last.StartsWith("0x", StringComparison.Ordinal))
        {
            // Looks like a number. Only a plain dotted quad is accepted, and then as an IP literal.
            if (labels.Length == 4 && labels.All(IsPlainOctet) && IPAddress.TryParse(ascii, out var v4))
            {
                result = new HostName(v4.ToString(), v4);
                return true;
            }

            return false;
        }

        result = new HostName(ascii, null);
        return true;
    }

    private static bool IsPlainOctet(string label) =>
        label.Length is >= 1 and <= 3
        && label.All(char.IsAsciiDigit)
        && (label.Length == 1 || label[0] != '0')
        && int.Parse(label, CultureInfo.InvariantCulture) <= 255;

    private static bool IsValidPunycode(string label)
    {
        try
        {
            return _idn.GetUnicode(label).Length > 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
