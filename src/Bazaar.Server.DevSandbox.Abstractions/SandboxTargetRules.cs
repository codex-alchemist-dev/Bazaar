using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Bazaar.Server.DevSandbox;

/// <summary>
/// The lexical target rules shared by the policy engine and every engine that pre-checks a call (doc 48 section 2.1 rule 1). No file
/// system or network access: pure string checks. A target that fails them is never allowed by any grant, including <c>*</c>.
/// </summary>
public static class SandboxTargetRules
{
    private const string _prefixSuffix = "/*";
    private const string _wildcard = "*";
    private const int _maxHostLength = 253;

    /// <summary>
    /// True when <paramref name="target"/> is a lexically clean path or opaque name: non-empty, no control, format or separator
    /// characters, no backslash, every segment non-empty and not made only of dots and spaces. One leading and one trailing
    /// <c>/</c> are allowed (the target <c>/</c> alone is the root).
    /// </summary>
    public static bool IsValidTarget(string? target)
    {
        if (string.IsNullOrEmpty(target))
        {
            return false;
        }

        foreach (var c in target)
        {
            if (c == '\\' || IsForbiddenChar(c))
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

    private static bool IsForbiddenChar(char c) =>
        char.GetUnicodeCategory(c) is UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator
            or UnicodeCategory.Surrogate
            or UnicodeCategory.PrivateUse
            or UnicodeCategory.OtherNotAssigned;

    public static bool GrantTargetMatches(string grantTarget, string actionTarget)
    {
        if (!IsValidTarget(actionTarget))
        {
            return false;
        }

        if (grantTarget is null)
        {
            return false;
        }

        if (grantTarget == _wildcard)
        {
            return true;
        }

        if (grantTarget.EndsWith(_prefixSuffix, StringComparison.Ordinal))
        {
            // "a/b/*": whole segments only. The base must itself be clean, and the action must continue below it after a "/".
            var baseTarget = grantTarget[..^_prefixSuffix.Length];
            if (baseTarget.Length > 0 && !IsValidTarget(baseTarget))
            {
                return false;
            }

            if (baseTarget.Length > 0 && baseTarget[^1] == '/')
            {
                return false;
            }

            return actionTarget.StartsWith(baseTarget + "/", StringComparison.Ordinal);
        }

        return string.Equals(grantTarget, actionTarget, StringComparison.Ordinal);
    }

    public static bool GrantHostMatches(string grantTarget, string normalizedHost)
    {
        if (grantTarget is null)
        {
            return false;
        }

        if (grantTarget == _wildcard)
        {
            return true;
        }

        return NormalizeHost(grantTarget) is { } grantHost && string.Equals(grantHost, normalizedHost, StringComparison.Ordinal);
    }

    /// <summary>
    /// Canonicalises a network target: ASCII-case-folded plain DNS name, or an IP literal whose text equals its canonical form.
    /// Returns null for anything else (trailing dot, port, userinfo, brackets, whitespace, non-ASCII, short or hex IPv4 forms).
    /// </summary>
    public static string? NormalizeHost(string? host)
    {
        if (string.IsNullOrEmpty(host) || host.Length > _maxHostLength)
        {
            return null;
        }

        foreach (var c in host)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-' or ':'))
            {
                return null;
            }
        }

        var folded = host.ToLowerInvariant();
        if (folded.Contains(':', StringComparison.Ordinal))
        {
            // Only an IPv6 literal may hold a colon; "host:443" does not parse and is refused.
            return IPAddress.TryParse(folded, out var v6)
                && v6.AddressFamily == AddressFamily.InterNetworkV6
                && v6.ScopeId == 0
                && string.Equals(v6.ToString(), folded, StringComparison.Ordinal)
                ? folded
                : null;
        }

        var labels = folded.Split('.');
        if (labels.All(l => l.Length > 0 && l.All(char.IsAsciiDigit)) && labels.Length == 4)
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

        // A name whose last label is all digits (or starts with 0x) is read as an IP by many resolvers: refuse it.
        var last = labels[^1];
        if (last.All(char.IsAsciiDigit) || last.StartsWith("0x", StringComparison.Ordinal))
        {
            return null;
        }

        return folded;
    }
}
