using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Bazaar.Server.VmEgress.Matching;

/// <summary>What an allowlist entry matches.</summary>
public enum AllowlistEntryKind
{
    /// <summary>One name.</summary>
    Exact,

    /// <summary>Every name below a suffix, never the suffix itself (<c>*.example.com</c>).</summary>
    Suffix,

    /// <summary>One IP literal, listed on purpose.</summary>
    IpLiteral,
}

/// <summary>
/// One parsed allowlist entry. Matching is on whole labels of the normalised name, so <c>evil-example.com</c> and
/// <c>example.com.evil.com</c> never match <c>*.example.com</c> or <c>example.com</c>.
/// </summary>
public sealed record AllowlistEntry
{
    private static readonly int[] _defaultPorts = [80, 443];

    private AllowlistEntry(AllowlistEntryKind kind, HostName host, int? port)
    {
        Kind = kind;
        Host = host;
        Port = port;
    }

    /// <summary>Kind of match.</summary>
    public AllowlistEntryKind Kind { get; }

    /// <summary>The name (without the wildcard) or the address.</summary>
    public HostName Host { get; }

    /// <summary>The one port this entry allows, or null for the default ports 80 and 443.</summary>
    public int? Port { get; }

    /// <summary>Canonical text of the entry. Equal entries have equal text, so it is safe to store and compare.</summary>
    public string Canonical
    {
        get
        {
            var name = Kind switch
            {
                AllowlistEntryKind.Suffix => "*." + Host.Name,
                AllowlistEntryKind.IpLiteral when Host.Address!.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 => "[" + Host.Name + "]",
                _ => Host.Name,
            };
            return Port is { } p ? name + ":" + p.ToString(CultureInfo.InvariantCulture) : name;
        }
    }

    /// <summary>Parses <c>host</c>, <c>*.suffix</c> or <c>[ipv6]</c>, each with an optional <c>:port</c>. A wildcard needs at least two labels after it.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out AllowlistEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(text) || text.Trim() != text)
        {
            return false;
        }

        string hostPart;
        int? port = null;
        if (text[0] == '[')
        {
            var close = text.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                return false;
            }

            hostPart = text[..(close + 1)];
            var rest = text[(close + 1)..];
            if (rest.Length > 0 && !TryPort(rest, out port))
            {
                return false;
            }
        }
        else
        {
            var colon = text.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0)
            {
                if (colon != text.LastIndexOf(':') || !TryPort(text[colon..], out port))
                {
                    return false;
                }

                hostPart = text[..colon];
            }
            else
            {
                hostPart = text;
            }
        }

        var wildcard = hostPart.StartsWith("*.", StringComparison.Ordinal);
        if (!HostName.TryNormalize(wildcard ? hostPart[2..] : hostPart, out var host))
        {
            return false;
        }

        if (host.IsIpLiteral)
        {
            if (wildcard)
            {
                return false;
            }

            entry = new AllowlistEntry(AllowlistEntryKind.IpLiteral, host, port);
            return true;
        }

        if (wildcard && !host.Name.Contains('.', StringComparison.Ordinal))
        {
            // *.com would allow a whole top level domain.
            return false;
        }

        entry = new AllowlistEntry(wildcard ? AllowlistEntryKind.Suffix : AllowlistEntryKind.Exact, host, port);
        return true;
    }

    /// <summary>Whether this entry allows a request for <paramref name="target"/> on <paramref name="targetPort"/>.</summary>
    public bool Matches(HostName target, int targetPort)
    {
        if (!AllowsPort(targetPort))
        {
            return false;
        }

        return Kind switch
        {
            AllowlistEntryKind.IpLiteral => target.IsIpLiteral && target.Address!.Equals(Host.Address),
            AllowlistEntryKind.Exact => !target.IsIpLiteral && string.Equals(target.Name, Host.Name, StringComparison.Ordinal),
            _ => !target.IsIpLiteral && target.Name.Length > Host.Name.Length + 1 && target.Name.EndsWith("." + Host.Name, StringComparison.Ordinal),
        };
    }

    /// <summary>True when everything <paramref name="other"/> allows is already allowed by this entry. Used to tell growth from a shrink.</summary>
    public bool Covers(AllowlistEntry other)
    {
        if (Port is { } p)
        {
            if (other.Port != p)
            {
                return false;
            }
        }
        else if (other.Port is { } op && !_defaultPorts.Contains(op))
        {
            return false;
        }

        return (Kind, other.Kind) switch
        {
            (AllowlistEntryKind.IpLiteral, AllowlistEntryKind.IpLiteral) => Host.Address!.Equals(other.Host.Address),
            (AllowlistEntryKind.Exact, AllowlistEntryKind.Exact) => Host.Name == other.Host.Name,
            (AllowlistEntryKind.Suffix, AllowlistEntryKind.Exact) => other.Host.Name.EndsWith("." + Host.Name, StringComparison.Ordinal),
            (AllowlistEntryKind.Suffix, AllowlistEntryKind.Suffix) =>
                other.Host.Name == Host.Name || other.Host.Name.EndsWith("." + Host.Name, StringComparison.Ordinal),
            _ => false,
        };
    }

    private bool AllowsPort(int port) => Port is { } p ? p == port : _defaultPorts.Contains(port);

    private static bool TryPort(string text, out int? port)
    {
        port = null;
        if (text.Length < 2 || text[0] != ':' || text.Length > 6 || text.AsSpan(1).ContainsAnyExceptInRange('0', '9'))
        {
            return false;
        }

        if (!int.TryParse(text.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value is < 1 or > 65535)
        {
            return false;
        }

        port = value;
        return true;
    }
}
