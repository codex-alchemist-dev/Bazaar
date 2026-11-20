using System.Text.RegularExpressions;

namespace Bazaar.Server.Council.Pilot;

/// <summary>
/// PROXY heuristic for "does this reason cite anything?" used by <see cref="FlipClassifier"/> (doc 10, Pilot metrics).
/// It is deterministic and cheap, and it does not judge reasoning quality. A follow-up reason counts as evidence-based
/// when it shares at least one token with the revealed reasons or the subject text. Tokens are lowercase words of at
/// least <see cref="MinWordLength"/> characters that are not stopwords, plus URL-like, issue or PR references
/// (<c>#123</c>), commit-hash-like strings and <c>[[wiki-links]]</c>.
/// Known limit: shared common vocabulary counts as citation, so evidence-free flips are undercounted. The real measure
/// is a reviewer-labelled sample of flips. Callers depend only on this class's two methods, so it can be replaced.
/// </summary>
public static partial class EvidenceCitation
{
    /// <summary>Minimum length of a word token.</summary>
    public const int MinWordLength = 4;

    private static readonly HashSet<string> _stopwords = new(StringComparer.Ordinal)
    {
        "about", "also", "because", "been", "being", "both", "could", "does", "done", "each", "even", "from",
        "have", "here", "into", "just", "make", "made", "more", "most", "only", "other", "over", "same", "should",
        "some", "such", "than", "that", "their", "them", "then", "there", "these", "they", "this", "those", "very",
        "were", "what", "when", "where", "which", "while", "will", "with", "would", "your",
    };

    /// <summary>Extracts the citable tokens of a text. Null or blank text yields an empty set.</summary>
    public static IReadOnlySet<string> Extract(string? text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return tokens;
        }

        var lower = text.ToLowerInvariant();

        foreach (Match word in WordPattern().Matches(lower))
        {
            if (word.Value.Length >= MinWordLength && !_stopwords.Contains(word.Value))
            {
                tokens.Add(word.Value);
            }
        }

        foreach (Match match in UrlPattern().Matches(lower))
        {
            tokens.Add(match.Value.TrimEnd('.', ',', ';', ')'));
        }

        foreach (Match match in IssueRefPattern().Matches(lower))
        {
            tokens.Add(match.Value);
        }

        foreach (Match match in CommitHashPattern().Matches(lower))
        {
            tokens.Add(match.Value);
        }

        foreach (Match match in WikiLinkPattern().Matches(lower))
        {
            tokens.Add(match.Value);
        }

        return tokens;
    }

    /// <summary>
    /// True when the follow-up reasoning shares at least one token with <paramref name="citable"/>, the union of the
    /// revealed reasons and the subject text. False means evidence-free under this proxy.
    /// </summary>
    public static bool IsEvidenceBased(string? followUpReasoning, IReadOnlySet<string> citable)
    {
        ArgumentNullException.ThrowIfNull(citable);
        return Extract(followUpReasoning).Any(citable.Contains);
    }

    [GeneratedRegex(@"[\p{L}\p{Nd}]+")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"https?://[^\s<>()]+")]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"(?<![\p{L}\p{Nd}])#\d+")]
    private static partial Regex IssueRefPattern();

    [GeneratedRegex(@"\b(?=[0-9a-f]*\d)(?=[0-9a-f]*[a-f])[0-9a-f]{7,40}\b")]
    private static partial Regex CommitHashPattern();

    [GeneratedRegex(@"\[\[[^\[\]\r\n]+\]\]")]
    private static partial Regex WikiLinkPattern();
}
