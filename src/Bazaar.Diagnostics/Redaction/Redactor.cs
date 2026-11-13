using System.Text;
using System.Text.RegularExpressions;

namespace Bazaar.Diagnostics.Redaction;

/// <summary>
/// On-device redaction (doc 43 section 6). A pure function of its input: no clock, network, gateway, NPU or
/// model. Layers: length limit, exact runtime values, pattern rules, residual scan. Any step that throws
/// turns the string into <see cref="RedactionMarkers.Failed"/>, so the caller holds the report.
/// </summary>
public sealed class Redactor
{
    /// <summary>Strings longer than this are cut before the pattern pass.</summary>
    public const int MaxStringLength = 4096;

    private readonly IReadOnlyList<RedactionRule> _rules;
    private readonly IReadOnlyList<string> _exactValues;

    /// <summary>Creates a redactor with rules v1. Exact values (for example the machine and user name) are replaced everywhere.</summary>
    public Redactor(IEnumerable<string>? exactValues = null)
        : this(RedactionRules.V1, exactValues)
    {
    }

    internal Redactor(IReadOnlyList<RedactionRule> rules, IEnumerable<string>? exactValues)
    {
        _rules = rules;

        // Longest first, so a value that contains another is replaced whole. The order does not depend on input order.
        _exactValues = (exactValues ?? [])
            .Where(value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(value => value.Length)
            .ThenBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Redacts one field. A <see cref="RedactionClass.Never"/> field is replaced whole and its text is not read.</summary>
    public RedactionResult Redact(RedactionClass fieldClass, string? input)
    {
        if (fieldClass == RedactionClass.Never)
        {
            return new RedactionResult(RedactionMarkers.Structural, new RedactionCounts(0, 0, 1, 0), RedactionStatus.Ok);
        }

        if (input is null)
        {
            return new RedactionResult(string.Empty, RedactionCounts.None, RedactionStatus.Ok);
        }

        try
        {
            var text = input;
            var truncated = 0;
            if (text.Length > MaxStringLength)
            {
                truncated = 1;
                text = text[..MaxStringLength] + RedactionMarkers.Truncated(input.Length - MaxStringLength);
            }

            var exact = 0;
            foreach (var value in _exactValues)
            {
                text = ReplaceExact(text, value, ref exact);
            }

            var pattern = 0;
            foreach (var rule in _rules)
            {
                var hits = 0;
                text = rule.Pattern.Replace(text, match =>
                {
                    var replacement = rule.Evaluator(match);
                    if (!string.Equals(replacement, match.Value, StringComparison.Ordinal))
                    {
                        hits++;
                    }

                    return replacement;
                });
                pattern += hits;
            }

            if (HasResidual(text))
            {
                return new RedactionResult(RedactionMarkers.Residual, RedactionCounts.None, RedactionStatus.Residual);
            }

            return new RedactionResult(text, new RedactionCounts(pattern, exact, 0, truncated), RedactionStatus.Ok);
        }
        catch (Exception)
        {
            // Fail closed: a step that throws (including a match timeout) never lets the original text through.
            return new RedactionResult(RedactionMarkers.Failed, RedactionCounts.None, RedactionStatus.Failed);
        }
    }

    /// <summary>
    /// True when the text still contains an exact value or a string that a rule would change. Run on the
    /// whole serialised report as the last layer (doc 43 section 6.2, layer 5).
    /// </summary>
    public bool HasResidual(string text)
    {
        foreach (var value in _exactValues)
        {
            if (text.Contains(value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var rule in _rules)
        {
            foreach (Match match in rule.Pattern.Matches(text))
            {
                if (!string.Equals(rule.Evaluator(match), match.Value, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string ReplaceExact(string text, string value, ref int count)
    {
        var index = text.IndexOf(value, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var start = 0;
        while (index >= 0)
        {
            builder.Append(text, start, index - start).Append(RedactionMarkers.Exact);
            count++;
            start = index + value.Length;
            index = text.IndexOf(value, start, StringComparison.OrdinalIgnoreCase);
        }

        builder.Append(text, start, text.Length - start);
        return builder.ToString();
    }
}
