// TODO(component): move to the Company component project when PR #125 merges.
using System.Text;

namespace Bazaar.Server.Company.Constitution.Propagation;

/// <summary>The excerpt of the Constitution for the system prefix, cut to a token budget.</summary>
/// <param name="Text">The excerpt text, including the truncation marker when the budget cut it.</param>
/// <param name="Truncated">True when the sections did not fit and the text was cut.</param>
/// <param name="EstimatedTokens">The estimated token count of <see cref="Text"/> (ceiling of characters / 4).</param>
public sealed record ExcerptResult(string Text, bool Truncated, int EstimatedTokens);

/// <summary>
/// Builds the Constitution excerpt for the system prefix and picks the commit a turn uses (doc 24, section 1).
/// </summary>
/// <remarks>
/// Token estimate: one token is treated as about four characters. This is a rough, provider-independent
/// estimate used only to bound the excerpt; it is not a tokenizer count. The budget is therefore a character
/// budget of <c>tokens * 4</c>. When the text is cut, the marker <c>[truncated]</c> is appended, and the marker
/// is counted inside the budget, so the excerpt never exceeds it.
/// </remarks>
public static class ExcerptRefresh
{
    /// <summary>The default for <c>constitution.excerpt_token_budget</c> (doc 24 Settings).</summary>
    public const int DefaultTokenBudget = 1500;

    /// <summary>Characters per estimated token.</summary>
    public const int CharsPerToken = 4;

    /// <summary>The text appended when the excerpt is cut.</summary>
    public const string TruncationMarker = "\n[truncated]";

    /// <summary>
    /// Concatenates the sections in enumeration order, each under a <c>## name</c> heading, and cuts the result at
    /// <paramref name="tokenBudget"/> tokens (see the type remarks for the estimate).
    /// </summary>
    /// <param name="sections">Section name to body text, in the order they appear in the Constitution.</param>
    /// <param name="tokenBudget">The token budget; must be positive.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tokenBudget"/> is not positive.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="sections"/> is null.</exception>
    public static ExcerptResult Build(IEnumerable<KeyValuePair<string, string>> sections, int tokenBudget = DefaultTokenBudget)
    {
        ArgumentNullException.ThrowIfNull(sections);

        if (tokenBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tokenBudget), tokenBudget, "The excerpt token budget must be positive.");
        }

        var full = new StringBuilder();
        foreach (var section in sections)
        {
            full.Append("## ").Append(section.Key).Append('\n').Append(section.Value).Append("\n\n");
        }

        var charBudget = (long)tokenBudget * CharsPerToken;
        var text = full.ToString();

        if (text.Length <= charBudget)
        {
            return new ExcerptResult(text, false, EstimateTokens(text));
        }

        var cut = charBudget <= TruncationMarker.Length
            ? TruncationMarker.Substring(0, (int)charBudget)
            : string.Concat(text.AsSpan(0, (int)charBudget - TruncationMarker.Length), TruncationMarker);

        return new ExcerptResult(cut, true, EstimateTokens(cut));
    }

    /// <summary>
    /// The commit a turn uses. The next turn starts on the active commit. A running turn keeps the commit it
    /// started with, even after the Constitution changed.
    /// </summary>
    /// <param name="turnCommitSha">The commit the turn started with.</param>
    /// <param name="activeCommitSha">The accepted commit now in force.</param>
    /// <param name="nextTurnStarts">False while the turn is running; true when the next turn begins.</param>
    /// <exception cref="ArgumentException">A sha is not 40 lowercase hex characters.</exception>
    public static string ForTurn(string turnCommitSha, string activeCommitSha, bool nextTurnStarts)
    {
        if (!ConstitutionChanged.IsValidSha(turnCommitSha))
        {
            throw new ArgumentException("The turn commit must be a 40-character lowercase sha.", nameof(turnCommitSha));
        }

        if (!ConstitutionChanged.IsValidSha(activeCommitSha))
        {
            throw new ArgumentException("The active commit must be a 40-character lowercase sha.", nameof(activeCommitSha));
        }

        return nextTurnStarts ? activeCommitSha : turnCommitSha;
    }

    private static int EstimateTokens(string text) => (text.Length + CharsPerToken - 1) / CharsPerToken;
}
