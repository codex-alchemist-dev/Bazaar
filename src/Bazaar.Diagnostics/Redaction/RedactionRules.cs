using System.Text.RegularExpressions;

namespace Bazaar.Diagnostics.Redaction;

/// <summary>
/// The built-in redaction rules, versioned as one set (doc 43 section 6.2, layer 2). Order matters: earlier
/// rules see the output of earlier ones. Every pattern runs with the non-backtracking engine and a timeout.
/// Changing a rule, its order or its markers changes <see cref="Version"/> and the corpus.
/// </summary>
public static class RedactionRules
{
    /// <summary>Version of the built-in rule set, written into every report.</summary>
    public const string Version = "1.0.0";

    private static readonly TimeSpan _matchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Rules v1, in application order.</summary>
    public static IReadOnlyList<RedactionRule> V1 { get; } =
    [
        new("secret.pem-block", Compile(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[A-Za-z0-9+/=\s]*(?:-----END [A-Z0-9 ]*PRIVATE KEY-----)?"), MaskAll),
        new("secret.github-token", Compile(@"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})\b"), MaskAll),
        new("secret.app-jwt", Compile(@"\beyJ[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}"), MaskAll),
        new("secret.provider-key", Compile(@"\bsk-(?:ant-)?[A-Za-z0-9_-]{20,}"), MaskAll),
        new("secret.aws-access-key", Compile(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b"), MaskAll),
        new("secret.authorization", Compile(@"\b((?:Proxy-)?Authorization\s*[:=]\s*)[^\r\n]+", ignoreCase: true), KeepGroupOneAndMask),
        new("secret.bearer", Compile(@"\bBearer\s+[A-Za-z0-9._~+/=-]+", ignoreCase: true), MaskAll),
        new("secret.url-userinfo", Compile(@"\b([a-z][a-z0-9+.-]*://)[^\s/@]+@", ignoreCase: true), UrlUserInfo),
        new("secret.sensitive-query", Compile(@"([?&](?:access_token|api_key|token|key|secret|sig|signature|password|code)=)[^&\s#""'<>]+", ignoreCase: true), KeepGroupOneAndMask),
        new("secret.keyed-assignment", Compile(@"\b((?:password|passwd|secret|token|api[_-]?key|client[_-]?secret)\s*[:=]\s*)[^\s,;""'&<>]+", ignoreCase: true), KeepGroupOneAndMask),
        new("pii.home-path", Compile(@"(?:[A-Za-z]:\\(?:Users|users)\\|/home/|/Users/)[^\\/\s""'<>|:*?]+"), HomePath),
        new("pii.email", Compile(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}"), MaskAll),
        new("pii.ipv4", Compile(@"\b(?:[0-9]{1,3}\.){3}[0-9]{1,3}\b"), KeepLoopbackV4),
        new("pii.ipv6", Compile(@"\b(?:[0-9a-f]{1,4}:){7}[0-9a-f]{1,4}\b|[0-9a-f]{0,4}(?::[0-9a-f]{0,4}){0,6}::(?:[0-9a-f]{1,4}(?::[0-9a-f]{0,4}){0,6})?", ignoreCase: true), KeepLoopbackV6),
        new("pii.mac", Compile(@"\b[0-9a-f]{2}(?:[:-][0-9a-f]{2}){5}\b", ignoreCase: true), MaskAll),
    ];

    private static Regex Compile(string pattern, bool ignoreCase = false) =>
        new(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), _matchTimeout);

    private static string MaskAll(Match match) => RedactionMarkers.Pattern;

    private static string KeepGroupOneAndMask(Match match) => match.Groups[1].Value + RedactionMarkers.Pattern;

    private static string UrlUserInfo(Match match) => match.Groups[1].Value + RedactionMarkers.Pattern + "@";

    private static string HomePath(Match match) => RedactionMarkers.Home;

    private static string KeepLoopbackV4(Match match) =>
        match.Value.StartsWith("127.", StringComparison.Ordinal) ? match.Value : RedactionMarkers.Pattern;

    // A match with no hex digit (a bare "::") is not an address and is kept. "::1" is the loopback and is kept.
    private static string KeepLoopbackV6(Match match) =>
        match.Value == "::1" || !match.Value.Any(char.IsAsciiHexDigit) ? match.Value : RedactionMarkers.Pattern;
}
