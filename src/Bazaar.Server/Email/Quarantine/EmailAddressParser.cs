using System.Net;
using System.Net.Sockets;

namespace Bazaar.Server.Email.Quarantine;

/// <summary>
/// An address split into its local part and domain. The domain is stored lowercased; the local part is kept as sent.
/// </summary>
/// <param name="LocalPart">The part before the @, as sent (quoted if it was quoted).</param>
/// <param name="Domain">The part after the @, lowercased. Either a host name or a bracketed address literal.</param>
public sealed record EmailAddress(string LocalPart, string Domain)
{
    /// <summary>The address as one string.</summary>
    public string Value => $"{LocalPart}@{Domain}";

    /// <summary>A key for comparing addresses. The local part is compared without case, which is the conservative choice for screening.</summary>
    public string ComparisonKey => $"{LocalPart.ToLowerInvariant()}@{Domain}";
}

/// <summary>A mailbox from a header such as From: an address, with the display name when one was given.</summary>
/// <param name="DisplayName">The display name with quotes removed, or null when the header was a bare address.</param>
/// <param name="Address">The address part.</param>
public sealed record ParsedMailbox(string? DisplayName, EmailAddress Address);

/// <summary>
/// Pure address parsing and normalisation (RFC 5321 limits and syntax). No DNS, no I/O.
/// The domain is checked for shape only; nothing here names or prefers a domain.
/// </summary>
public static class EmailAddressParser
{
    private const int _maxAddressLength = 254;
    private const int _maxLocalPartLength = 64;
    private const int _maxDomainLength = 255;
    private const int _maxLabelLength = 63;

    /// <summary>Parses an addr-spec such as <c>alice@example.org</c>. Surrounding whitespace is ignored.</summary>
    /// <param name="text">The text to parse.</param>
    /// <param name="address">The parsed address when the method returns true; null otherwise.</param>
    public static bool TryParse(string? text, out EmailAddress? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        if (value.Length > _maxAddressLength)
        {
            return false;
        }

        var at = FindSeparator(value);
        if (at < 0)
        {
            return false;
        }

        var local = value[..at];
        var domain = value[(at + 1)..];
        if (!IsValidLocalPart(local) || !TryNormaliseDomain(domain, out var normalisedDomain))
        {
            return false;
        }

        address = new EmailAddress(local, normalisedDomain);
        return true;
    }

    /// <summary>
    /// Parses a mailbox header value: either a bare address or <c>Display Name &lt;address&gt;</c>, with the name optionally quoted.
    /// </summary>
    /// <param name="header">The header value.</param>
    /// <param name="mailbox">The parsed mailbox when the method returns true; null otherwise.</param>
    public static bool TryParseMailbox(string? header, out ParsedMailbox? mailbox)
    {
        mailbox = null;
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        var value = header.Trim();
        if (!value.EndsWith('>'))
        {
            if (!TryParse(value, out var bare))
            {
                return false;
            }

            mailbox = new ParsedMailbox(null, bare!);
            return true;
        }

        var open = value.LastIndexOf('<');
        if (open < 0 || !TryParse(value[(open + 1)..^1], out var address))
        {
            return false;
        }

        var display = Unquote(value[..open].Trim());
        mailbox = new ParsedMailbox(display.Length == 0 ? null : display, address!);
        return true;
    }

    private static int FindSeparator(string value)
    {
        if (value.StartsWith('"'))
        {
            var end = QuotedStringEnd(value, 0);
            return end >= 0 && end + 1 < value.Length && value[end + 1] == '@' ? end + 1 : -1;
        }

        return value.LastIndexOf('@');
    }

    /// <summary>Index of the closing quote of a quoted string that starts at <paramref name="start"/>, or -1.</summary>
    private static int QuotedStringEnd(string value, int start)
    {
        var i = start + 1;
        while (i < value.Length)
        {
            var c = value[i];
            if (c == '\\')
            {
                i += 2;
                continue;
            }

            if (c == '"')
            {
                return i;
            }

            if (c < 32 || c > 126)
            {
                return -1;
            }

            i++;
        }

        return -1;
    }

    private static bool IsValidLocalPart(string local)
    {
        if (local.Length == 0 || local.Length > _maxLocalPartLength)
        {
            return false;
        }

        if (local[0] == '"')
        {
            return QuotedStringEnd(local, 0) == local.Length - 1;
        }

        foreach (var atom in local.Split('.'))
        {
            if (atom.Length == 0 || !atom.All(IsAtext))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAtext(char c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9'
            or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '/' or '='
            or '?' or '^' or '_' or '`' or '{' or '|' or '}' or '~';

    private static bool TryNormaliseDomain(string domain, out string normalised)
    {
        normalised = string.Empty;
        if (domain.Length == 0 || domain.Length > _maxDomainLength)
        {
            return false;
        }

        if (domain[0] == '[')
        {
            return TryAddressLiteral(domain, out normalised);
        }

        var labels = domain.Split('.');
        if (labels.Length < 2 || !labels.All(IsValidLabel))
        {
            return false;
        }

        // A top-level label that is all digits looks like an IPv4 address, not a domain.
        if (labels[^1].All(char.IsAsciiDigit))
        {
            return false;
        }

        normalised = domain.ToLowerInvariant();
        return true;
    }

    private static bool IsValidLabel(string label)
    {
        if (label.Length == 0 || label.Length > _maxLabelLength || label[0] == '-' || label[^1] == '-')
        {
            return false;
        }

        return label.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
    }

    private static bool TryAddressLiteral(string domain, out string normalised)
    {
        normalised = string.Empty;
        if (domain.Length < 3 || domain[^1] != ']')
        {
            return false;
        }

        var inner = domain[1..^1];
        if (inner.StartsWith("IPv6:", StringComparison.OrdinalIgnoreCase))
        {
            if (!IPAddress.TryParse(inner[5..], out var v6) || v6.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return false;
            }

            normalised = "[IPv6:" + v6 + "]";
            return true;
        }

        // Only dotted-quad IPv4 text, so IPAddress.TryParse shorthand such as "1" or "1.2" is rejected.
        if (inner.Count(c => c == '.') != 3 || !inner.All(c => c == '.' || char.IsAsciiDigit(c)))
        {
            return false;
        }

        if (!IPAddress.TryParse(inner, out var v4) || v4.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        normalised = "[" + v4 + "]";
        return true;
    }

    private static string Unquote(string text)
    {
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            return text[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        return text;
    }
}
