using System.Text;
using System.Text.RegularExpressions;

namespace Bazaar.Server.Souls.Bundles;

/// <summary>One match from the secret scan. Carries the rule and location only, never the matched text.</summary>
/// <param name="Path">Bundle-relative path of the file.</param>
/// <param name="RuleId">The rule that matched (for example "github-token").</param>
/// <param name="Line">1-based line number of the match.</param>
public sealed record SecretFinding(string Path, string RuleId, int Line);

/// <summary>
/// Scans file bytes for secret shapes before export (docs/design/07-soul-system.md, "Export"). Pattern-based and
/// best-effort: it catches known key formats, bearer tokens and high-entropy credential assignments, not every secret.
/// </summary>
public static class BundleSecretScanner
{
    private static readonly TimeSpan _matchTimeout = TimeSpan.FromSeconds(2);

    // Each key shape has a left boundary so that ordinary words (for example "risk-assessment") do not match "sk-".
    private static readonly (string RuleId, Regex Pattern)[] _shapes =
    [
        ("private-key-pem", new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.CultureInvariant, _matchTimeout)),
        ("anthropic-api-key", new Regex(@"(?<![A-Za-z0-9])sk-ant-[A-Za-z0-9_-]{10,}", RegexOptions.CultureInvariant, _matchTimeout)),
        ("openai-style-api-key", new Regex(@"(?<![A-Za-z0-9])sk-[A-Za-z0-9_-]{16,}", RegexOptions.CultureInvariant, _matchTimeout)),
        ("github-token", new Regex(@"(?<![A-Za-z0-9])gh[pousr]_[A-Za-z0-9]{20,}", RegexOptions.CultureInvariant, _matchTimeout)),
        ("github-fine-grained-pat", new Regex(@"(?<![A-Za-z0-9])github_pat_[A-Za-z0-9_]{20,}", RegexOptions.CultureInvariant, _matchTimeout)),
        ("aws-access-key-id", new Regex(@"(?<![A-Z0-9])AKIA[0-9A-Z]{16}(?![A-Z0-9])", RegexOptions.CultureInvariant, _matchTimeout)),
        ("bearer-token", new Regex(@"(?i)\bbearer\s+[A-Za-z0-9._~+/=-]{12,}", RegexOptions.CultureInvariant, _matchTimeout)),
    ];

    // Credential-style assignment. The value must be at least 8 characters and have Shannon entropy of at least 3 bits per character.
    private static readonly Regex _assignment = new(
        @"(?i)\b(?:password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key)\s*[:=]\s*[""']?([^\s""',;]{8,})",
        RegexOptions.CultureInvariant,
        _matchTimeout);

    private const double _minEntropyBits = 3.0;
    private const string _assignmentRuleId = "credential-assignment";

    /// <summary>Returns every finding in the file, in the order they appear. An empty list means the file is clean.</summary>
    public static IReadOnlyList<SecretFinding> Scan(string path, ReadOnlySpan<byte> content)
    {
        ArgumentNullException.ThrowIfNull(path);
        var text = Encoding.UTF8.GetString(content);
        var findings = new List<(int Index, SecretFinding Finding)>();

        foreach (var (ruleId, pattern) in _shapes)
        {
            foreach (Match match in pattern.Matches(text))
                findings.Add((match.Index, new SecretFinding(path, ruleId, LineOf(text, match.Index))));
        }

        foreach (Match match in _assignment.Matches(text))
        {
            var value = match.Groups[1];
            if (ShannonBits(value.Value) >= _minEntropyBits)
                findings.Add((match.Index, new SecretFinding(path, _assignmentRuleId, LineOf(text, match.Index))));
        }

        return findings.OrderBy(f => f.Index).Select(f => f.Finding).ToList();
    }

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    private static double ShannonBits(string value)
    {
        var counts = new Dictionary<char, int>();
        foreach (var c in value)
            counts[c] = counts.GetValueOrDefault(c) + 1;

        double bits = 0;
        foreach (var count in counts.Values)
        {
            var p = (double)count / value.Length;
            bits -= p * Math.Log2(p);
        }

        return bits;
    }
}

/// <summary>
/// Thrown when an export is refused because a file matched the secret scan. The message names files, lines and rules only;
/// it never includes the matched text.
/// </summary>
public sealed class BundleSecretScanException : Exception
{
    /// <summary>Creates the exception. <paramref name="findings"/> must not be empty.</summary>
    public BundleSecretScanException(IReadOnlyList<SecretFinding> findings)
        : base(Describe(findings))
    {
        ArgumentNullException.ThrowIfNull(findings);
        if (findings.Count == 0)
            throw new ArgumentException("At least one finding is required.", nameof(findings));
        Findings = findings;
    }

    /// <summary>Every match, in the order found.</summary>
    public IReadOnlyList<SecretFinding> Findings { get; }

    private static string Describe(IReadOnlyList<SecretFinding> findings)
    {
        var parts = findings.Select(f => $"'{f.Path}' line {f.Line} (rule {f.RuleId})");
        return "Bundle export refused: secret pattern matched in " + string.Join("; ", parts) + ". Matched values are not reported.";
    }
}
