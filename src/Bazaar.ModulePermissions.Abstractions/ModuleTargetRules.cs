using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Bazaar.ModulePermissions;

/// <summary>
/// The lexical rules for what a permission scope may name and how a request is compared with it (doc 48 section 1). Pure
/// string checks: no file system or network access. A target that fails them is covered by no grant.
/// </summary>
public static class ModuleTargetRules
{
    /// <summary>Longest target accepted, in characters.</summary>
    public const int MaxTargetLength = 4096;

    private const string _prefixSuffix = "/*";
    private const int _maxHostLength = 253;

    /// <summary>
    /// True when <paramref name="target"/> is a clean path or name: not empty, no control, format, separator, surrogate or
    /// unassigned characters, no backslash, no <c>*</c>, and every <c>/</c>-separated segment is non-empty and not made only of
    /// dots and spaces (so <c>.</c>, <c>..</c> and <c>//</c> never pass). One leading and one trailing <c>/</c> are allowed.
    /// </summary>
    public static bool IsValidTarget(string? target)
    {
        if (string.IsNullOrEmpty(target) || target.Length > MaxTargetLength)
        {
            return false;
        }

        foreach (var c in target)
        {
            if (c is '\\' or '*' || IsForbidden(c))
            {
                return false;
            }
        }

        if (target == "/")
        {
            return true;
        }

        var span = target.AsSpan();
        if (span[0] == '/')
        {
            span = span[1..];
        }

        if (span.Length > 0 && span[^1] == '/')
        {
            span = span[..^1];
        }

        if (span.Length == 0)
        {
            return false;
        }

        foreach (var range in span.Split('/'))
        {
            var segment = span[range];
            if (segment.Length == 0 || segment.IndexOfAnyExcept('.', ' ') < 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True when <paramref name="scope"/> may stand in a permission: a valid target, or a valid target followed by <c>/*</c>
    /// (everything below it). A bare <c>*</c> is not a scope: a module names what it wants.
    /// </summary>
    public static bool IsValidScope(string? scope)
    {
        if (scope is null)
        {
            return false;
        }

        if (scope.EndsWith(_prefixSuffix, StringComparison.Ordinal))
        {
            var prefix = scope[..^_prefixSuffix.Length];
            return IsValidTarget(prefix) && prefix[^1] != '/';
        }

        return IsValidTarget(scope);
    }

    /// <summary>
    /// Whether a granted scope covers a requested target. Exact scopes compare ordinally. A scope ending in <c>/*</c> covers
    /// targets below its prefix by whole segments: <c>a/b/*</c> covers <c>a/b/c</c>, not <c>a/b</c> itself and not <c>a/bc</c>.
    /// </summary>
    public static bool ScopeCovers(string scope, string target)
    {
        if (!IsValidScope(scope) || !IsValidTarget(target))
        {
            return false;
        }

        if (scope.EndsWith(_prefixSuffix, StringComparison.Ordinal))
        {
            // Keep the slash of "/*": the target must continue below the prefix after a separator.
            var prefixWithSlash = scope.AsSpan(0, scope.Length - 1);
            return target.AsSpan().StartsWith(prefixWithSlash, StringComparison.Ordinal) && target.Length > prefixWithSlash.Length;
        }

        return string.Equals(scope, target, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one spelling of a network host: an ASCII DNS name in lower case, or an IP literal whose text is already canonical.
    /// Null for anything else (a port, userinfo, brackets, a trailing dot, whitespace, non-ASCII, short or hexadecimal IPv4
    /// forms, a scoped or IPv4-mapped IPv6 address), so two spellings of one host can never disagree about a grant.
    /// </summary>
    public static string? NormalizeHost(string? host)
    {
        if (string.IsNullOrEmpty(host) || host.Length > _maxHostLength)
        {
            return null;
        }

        foreach (var c in host)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':'))
            {
                return null;
            }
        }

        var folded = host.ToLowerInvariant();
        if (folded.Contains(':', StringComparison.Ordinal))
        {
            return IPAddress.TryParse(folded, out var v6)
                && v6.AddressFamily == AddressFamily.InterNetworkV6
                && v6.ScopeId == 0
                && !v6.IsIPv4MappedToIPv6
                && string.Equals(v6.ToString(), folded, StringComparison.Ordinal)
                ? folded
                : null;
        }

        var labels = folded.Split('.');
        if (labels.Length == 4 && labels.All(l => l.Length > 0 && l.All(char.IsAsciiDigit)))
        {
            return IPAddress.TryParse(folded, out var v4) && string.Equals(v4.ToString(), folded, StringComparison.Ordinal) ? folded : null;
        }

        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-')
            {
                return null;
            }
        }

        // Many resolvers read a name whose last label is a number (or starts with 0x) as an IP address.
        var last = labels[^1];
        return last.All(char.IsAsciiDigit) || last.StartsWith("0x", StringComparison.Ordinal) ? null : folded;
    }

    // U+FFFD is what a lossy decode leaves behind: the original bytes are unknown, so the name is not trusted.
    private static bool IsForbidden(char c) =>
        c == '\uFFFD' || char.GetUnicodeCategory(c) is UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator
            or UnicodeCategory.Surrogate
            or UnicodeCategory.PrivateUse
            or UnicodeCategory.OtherNotAssigned;
}
