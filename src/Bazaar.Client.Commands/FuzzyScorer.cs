using Bazaar.Client.Commands.Fuzzy;

namespace Bazaar.Client.Commands;

/// <summary>A successful subsequence match. Positions are indexes into the candidate text.</summary>
public sealed record FuzzyResult(int Score, IReadOnlyList<int> Positions);

/// <summary>
/// Case-insensitive subsequence scoring for the command palette. A facade over <see cref="FuzzyCore"/>, the shared matching core.
/// </summary>
public static class FuzzyScorer
{
    /// <summary>Returns null when the query is not a subsequence of the candidate.
    /// A blank query matches everything with score 0 and no positions.</summary>
    public static FuzzyResult? Match(string query, string candidate) => FuzzyCore.Match(query, candidate);
}
