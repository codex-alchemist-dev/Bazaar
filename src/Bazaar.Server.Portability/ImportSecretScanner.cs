using System.Text;
using System.Text.RegularExpressions;

namespace Bazaar.Server.Portability;

/// <summary>Which field of a record a finding is in.</summary>
public enum SecretField
{
    Title,
    Body,
}

/// <summary>
/// One secret-shaped string found in a record (doc 44 section 8.5). It holds the rule, the field and the 1-based
/// line, never the secret itself, so a finding can be shown and audited safely.
/// </summary>
public sealed record SecretFinding(string RuleId, SecretField Field, int Line);

/// <summary>
/// Looks for key-shaped strings, private key blocks and connection strings in imported text (doc 44 section 8.5).
/// Pure. A pattern that times out counts as a finding (rule <see cref="TimeoutRuleId"/>), so a crafted input cannot
/// slip past by being slow to scan.
/// </summary>
public static class ImportSecretScanner
{
    /// <summary>Rule id used when a pattern times out. The record is treated as flagged.</summary>
    public const string TimeoutRuleId = "scan-timeout";

    private static readonly TimeSpan _matchTimeout = TimeSpan.FromMilliseconds(250);
    private const double _minEntropyBits = 3.0;
    private const string _assignmentRuleId = "credential-assignment";

    private static readonly (string RuleId, Regex Pattern, int Group)[] _rules =
    [
        ("private-key-block", Make(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(?:-----END [A-Z ]*PRIVATE KEY-----|\z)"), 0),
        ("anthropic-api-key", Make(@"(?<![A-Za-z0-9])sk-ant-[A-Za-z0-9_-]{10,}"), 0),
        ("openai-style-api-key", Make(@"(?<![A-Za-z0-9])sk-[A-Za-z0-9_-]{16,}"), 0),
        ("github-token", Make(@"(?<![A-Za-z0-9])gh[pousr]_[A-Za-z0-9]{20,}"), 0),
        ("github-fine-grained-pat", Make(@"(?<![A-Za-z0-9])github_pat_[A-Za-z0-9_]{20,}"), 0),
        ("aws-access-key-id", Make(@"(?<![A-Z0-9])AKIA[0-9A-Z]{16}(?![A-Z0-9])"), 0),
        ("google-api-key", Make(@"(?<![A-Za-z0-9_-])AIza[0-9A-Za-z_-]{35}"), 0),
        ("slack-token", Make(@"(?<![A-Za-z0-9])xox[baprs]-[A-Za-z0-9-]{10,}"), 0),
        ("stripe-key", Make(@"(?<![A-Za-z0-9])[rs]k_(?:live|test)_[A-Za-z0-9]{16,}"), 0),
        ("json-web-token", Make(@"(?<![A-Za-z0-9_-])eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}"), 0),
        ("bearer-token", Make(@"(?i)\bbearer\s+([A-Za-z0-9._~+/=-]{12,})"), 1),
        ("connection-string-url", Make(@"(?i)\b(?:postgres(?:ql)?|mysql|mariadb|mongodb(?:\+srv)?|rediss?|amqps?|mssql|sqlserver)://[^\s/:@]+:[^\s/@]+@\S+"), 0),
        ("connection-string-password", Make(@"(?i)\b(?:server|data source|host)\s*=[^;\r\n]*;[^\r\n]*?\b(?:password|pwd)\s*=\s*([^;\s]+)"), 1),
    ];

    // Credential-style assignment. The value must be at least 8 characters and carry enough entropy to look like a secret.
    private static readonly Regex _assignment = Make(
        @"(?i)\b(?:password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key)\s*[:=]\s*[""']?([^\s""',;]{8,})");

    /// <summary>Returns every finding in the record, title first, then body in line order.</summary>
    public static IReadOnlyList<SecretFinding> Scan(SourceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var findings = new List<SecretFinding>();
        foreach (var span in SpansOf(record.Title))
            findings.Add(new SecretFinding(span.RuleId, SecretField.Title, LineOf(record.Title, span.Start)));
        foreach (var span in SpansOf(record.Body))
            findings.Add(new SecretFinding(span.RuleId, SecretField.Body, LineOf(record.Body, span.Start)));
        return findings;
    }

    /// <summary>Replaces each secret-shaped span with <c>[REDACTED:rule]</c>. Pure and deterministic.</summary>
    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var spans = SpansOf(text);
        if (spans.Count == 0) return text;

        // A timed-out scan has no reliable span, so the whole text is withheld instead of passed through.
        if (spans.Any(s => s.RuleId == TimeoutRuleId)) return "[REDACTED:" + TimeoutRuleId + "]";

        // Overlapping spans are merged first, so a partly overlapping match never leaves a secret fragment behind.
        var merged = new List<Span>();
        foreach (var span in spans)
        {
            if (merged.Count > 0 && span.Start < merged[^1].Start + merged[^1].Length)
            {
                var last = merged[^1];
                var end = Math.Max(last.Start + last.Length, span.Start + span.Length);
                merged[^1] = last with { Length = end - last.Start };
            }
            else
            {
                merged.Add(span);
            }
        }

        var builder = new StringBuilder();
        var cursor = 0;
        foreach (var span in merged)
        {
            builder.Append(text, cursor, span.Start - cursor);
            builder.Append("[REDACTED:").Append(span.RuleId).Append(']');
            cursor = span.Start + span.Length;
        }
        builder.Append(text, cursor, text.Length - cursor);
        return builder.ToString();
    }

    private readonly record struct Span(string RuleId, int Start, int Length);

    private static List<Span> SpansOf(string text)
    {
        var spans = new List<Span>();
        try
        {
            foreach (var (ruleId, pattern, group) in _rules)
            {
                foreach (Match match in pattern.Matches(text))
                {
                    var part = match.Groups[group];
                    if (part.Success && part.Length > 0) spans.Add(new Span(ruleId, part.Index, part.Length));
                }
            }

            foreach (Match match in _assignment.Matches(text))
            {
                var value = match.Groups[1];
                if (ShannonBits(value.Value) >= _minEntropyBits)
                    spans.Add(new Span(_assignmentRuleId, value.Index, value.Length));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            spans.Add(new Span(TimeoutRuleId, 0, text.Length));
        }

        spans.Sort((a, b) =>
        {
            var byStart = a.Start.CompareTo(b.Start);
            return byStart != 0 ? byStart : b.Length.CompareTo(a.Length);
        });
        return spans;
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n') line++;
        }
        return line;
    }

    private static double ShannonBits(string value)
    {
        if (value.Length == 0) return 0;
        var counts = new Dictionary<char, int>();
        foreach (var c in value) counts[c] = counts.GetValueOrDefault(c) + 1;
        var bits = 0.0;
        foreach (var count in counts.Values)
        {
            var p = (double)count / value.Length;
            bits -= p * Math.Log2(p);
        }
        return bits;
    }

    private static Regex Make(string pattern) => new(pattern, RegexOptions.CultureInvariant, _matchTimeout);
}
