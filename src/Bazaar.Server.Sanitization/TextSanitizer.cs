using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;

namespace Bazaar.Server.Sanitization;

/// <summary>
/// Pure text sanitizer for the Psych Ward and fun systems (doc 26). Candidate spans come from fixed rules and the
/// caller's terms, all matched against the original text. Overlaps keep the longest span, then the earlier rule.
/// No I/O, no state.
/// </summary>
public sealed class TextSanitizer : ITextSanitizer
{
    private const RegexOptions _options = RegexOptions.CultureInvariant;
    private static readonly TimeSpan _matchTimeout = TimeSpan.FromSeconds(2);
    private const string _wordBefore = @"(?<![\p{L}\p{N}_])";
    private const string _wordAfter = @"(?![\p{L}\p{N}_])";

    private static readonly Rule[] _rules =
    [
        new(new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----", _options, _matchTimeout), RedactionKind.Secret, 1),
        new(new Regex(@"sk-ant-[A-Za-z0-9_-]{10,}", _options, _matchTimeout), RedactionKind.Secret, 2),
        new(new Regex(@"sk-[A-Za-z0-9_-]{16,}", _options, _matchTimeout), RedactionKind.Secret, 2),
        new(new Regex(@"gh[pousr]_[A-Za-z0-9]{20,}", _options, _matchTimeout), RedactionKind.Secret, 2),
        new(new Regex(@"github_pat_[A-Za-z0-9_]{20,}", _options, _matchTimeout), RedactionKind.Secret, 2),
        new(new Regex(@"AKIA[0-9A-Z]{16}", _options, _matchTimeout), RedactionKind.Secret, 2),
        new(new Regex(@"xox[abprs]-[A-Za-z0-9-]{10,}", _options, _matchTimeout), RedactionKind.Secret, 2),
        new(new Regex(@"AIza[0-9A-Za-z_-]{35}", _options, _matchTimeout), RedactionKind.Secret, 2),
        new(new Regex(@"eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}", _options, _matchTimeout), RedactionKind.Secret, 2),
        new(new Regex(@"(?i)\bbearer\s+[A-Za-z0-9._~+/=-]{12,}", _options, _matchTimeout), RedactionKind.Secret, 3),
        new(new Regex(@"(?i)\b(?:password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key)\s*[:=]\s*(?:""[^""\r\n]*""|'[^'\r\n]*'|\S+)", _options, _matchTimeout), RedactionKind.Secret, 4),
        new(new Regex(@"(?<=://)[^\s/:@]+:[^\s/@]+(?=@)", _options, _matchTimeout), RedactionKind.Secret, 5),
        new(new Regex(@"(?m)^[ \t]*>[^\r\n]*(?:\r?\n[ \t]*>[^\r\n]*)*", _options, _matchTimeout), RedactionKind.QuotedText, 6),
        new(new Regex(@"\[\[[^\[\]\r\n]+\]\]", _options, _matchTimeout), RedactionKind.VaultLink, 8),
        new(new Regex(@"(?<![\w/.-])(?:[\w.-]+/)+[\w.-]+\.md\b", _options, _matchTimeout), RedactionKind.VaultPath, 9),
        new(new Regex(@"(?<![\w/.-])(?:vault|memory)/[^\s)\]""'`<>]+", _options, _matchTimeout), RedactionKind.VaultPath, 9),
        new(new Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}", _options, _matchTimeout), RedactionKind.Email, 10),
    ];

    /// <inheritdoc />
    public SanitizedText Sanitize(string text, SensitiveTerms terms)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(terms.People, nameof(terms));
        ArgumentNullException.ThrowIfNull(terms.Agents, nameof(terms));
        ArgumentNullException.ThrowIfNull(terms.Repositories, nameof(terms));
        ArgumentNullException.ThrowIfNull(terms.Customers, nameof(terms));
        ArgumentNullException.ThrowIfNull(terms.ExternalQuotes, nameof(terms));

        var candidates = new List<Candidate>();
        foreach (var rule in _rules)
        {
            AddMatches(candidates, text, rule.Pattern, rule.Kind, rule.Priority);
        }

        AddTermMatches(candidates, text, terms.ExternalQuotes, RedactionKind.ExternalQuote, 7, wholeWord: false);
        AddTermMatches(candidates, text, terms.People, RedactionKind.Person, 11, wholeWord: true);
        AddTermMatches(candidates, text, terms.Agents, RedactionKind.Agent, 12, wholeWord: true);
        AddTermMatches(candidates, text, terms.Repositories, RedactionKind.Repository, 13, wholeWord: true);
        AddTermMatches(candidates, text, terms.Customers, RedactionKind.Customer, 14, wholeWord: true);

        return Build(text, candidates);
    }

    private static void AddMatches(List<Candidate> candidates, string text, Regex pattern, RedactionKind kind, int priority)
    {
        foreach (Match match in pattern.Matches(text))
        {
            candidates.Add(new Candidate(match.Index, match.Length, kind, priority));
        }
    }

    private static void AddTermMatches(List<Candidate> candidates, string text, IEnumerable<string> terms, RedactionKind kind, int priority, bool wholeWord)
    {
        foreach (var raw in terms)
        {
            var term = raw?.Trim();
            if (string.IsNullOrEmpty(term))
            {
                continue;
            }

            var pattern = wholeWord
                ? _wordBefore + Regex.Escape(term) + _wordAfter
                : Regex.Escape(term);
            var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, _matchTimeout);
            AddMatches(candidates, text, regex, kind, priority);
        }
    }

    private static SanitizedText Build(string text, List<Candidate> candidates)
    {
        // Longest span wins at a shared start, then the earlier rule. A span may not overlap a kept span.
        var ordered = candidates
            .OrderBy(candidate => candidate.Start)
            .ThenByDescending(candidate => candidate.Length)
            .ThenBy(candidate => candidate.Priority);

        var kept = new List<Candidate>();
        var end = 0;
        foreach (var candidate in ordered)
        {
            if (candidate.Start < end)
            {
                continue;
            }

            kept.Add(candidate);
            end = candidate.Start + candidate.Length;
        }

        if (kept.Count == 0)
        {
            return new SanitizedText(text, ImmutableArray<Redaction>.Empty);
        }

        var counts = new int[Enum.GetValues<RedactionKind>().Length];
        var builder = new StringBuilder(text.Length);
        var position = 0;
        foreach (var candidate in kept)
        {
            builder.Append(text, position, candidate.Start - position);
            builder.Append(Placeholder(candidate.Kind));
            counts[(int)candidate.Kind]++;
            position = candidate.Start + candidate.Length;
        }

        builder.Append(text, position, text.Length - position);

        var redactions = ImmutableArray.CreateBuilder<Redaction>();
        for (var index = 0; index < counts.Length; index++)
        {
            if (counts[index] > 0)
            {
                redactions.Add(new Redaction((RedactionKind)index, counts[index]));
            }
        }

        return new SanitizedText(builder.ToString(), redactions.ToImmutable());
    }

    private static string Placeholder(RedactionKind kind) => kind switch
    {
        RedactionKind.Person => "[person]",
        RedactionKind.Agent => "[agent]",
        RedactionKind.Repository => "[repo]",
        RedactionKind.Customer => "[customer]",
        RedactionKind.Email => "[email]",
        RedactionKind.VaultPath => "[vault-path]",
        RedactionKind.VaultLink => "[vault-link]",
        RedactionKind.Secret => "[secret]",
        RedactionKind.QuotedText => "[quoted text removed]",
        RedactionKind.ExternalQuote => "[external quote removed]",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown redaction kind."),
    };

    private readonly record struct Rule(Regex Pattern, RedactionKind Kind, int Priority);

    private readonly record struct Candidate(int Start, int Length, RedactionKind Kind, int Priority);
}
