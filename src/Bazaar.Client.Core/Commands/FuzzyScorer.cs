namespace Bazaar.Client.Core.Commands;

/// <summary>A successful subsequence match. Positions are indexes into the candidate text.</summary>
public sealed record FuzzyResult(int Score, IReadOnlyList<int> Positions);

/// <summary>
/// Case-insensitive subsequence scoring. Every query character must appear in order.
/// Rewards consecutive runs and word-start hits, penalises gaps and length.
/// The best alignment is found by dynamic programming, not by greedy matching.
/// </summary>
public static class FuzzyScorer
{
    private const int _matchPoints = 16;
    private const int _consecutiveBonus = 8;
    private const int _wordStartBonus = 10;
    private const int _gapPenalty = 1;
    private const int _noScore = int.MinValue / 4;

    /// <summary>Returns null when the query is not a subsequence of the candidate.
    /// A blank query matches everything with score 0 and no positions.</summary>
    public static FuzzyResult? Match(string query, string candidate)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidate);

        var q = query.Trim();
        if (q.Length == 0)
        {
            return new FuzzyResult(0, []);
        }

        int m = q.Length;
        int n = candidate.Length;
        if (m > n)
        {
            return null;
        }

        // best[i][j]: best score matching q[0..i] with q[i] placed at candidate[j].
        // from[i][j]: the candidate index of q[i-1] on that best path.
        var best = new int[m][];
        var from = new int[m][];
        for (int i = 0; i < m; i++)
        {
            best[i] = new int[n];
            from[i] = new int[n];
            Array.Fill(best[i], _noScore);
            Array.Fill(from[i], -1);
        }

        for (int i = 0; i < m; i++)
        {
            // Running maximum over k <= j-2 of best[i-1][k] + (k+1) * _gapPenalty.
            // Covers every non-consecutive predecessor at a gap cost of (j-k-1) * _gapPenalty.
            int runValue = _noScore;
            int runIndex = -1;

            for (int j = 0; j < n; j++)
            {
                if (i > 0 && j >= 2 && best[i - 1][j - 2] > _noScore)
                {
                    int k = j - 2;
                    int value = best[i - 1][k] + (k + 1) * _gapPenalty;
                    if (value > runValue)
                    {
                        runValue = value;
                        runIndex = k;
                    }
                }

                if (!SameChar(q[i], candidate[j]))
                {
                    continue;
                }

                int point = _matchPoints + (IsWordStart(candidate, j) ? _wordStartBonus : 0);

                if (i == 0)
                {
                    // Leading characters skipped before the first match count as gap.
                    best[0][j] = point - j * _gapPenalty;
                    continue;
                }

                int bestPrev = _noScore;
                int bestFrom = -1;

                if (j >= 1 && best[i - 1][j - 1] > _noScore)
                {
                    bestPrev = best[i - 1][j - 1] + _consecutiveBonus;
                    bestFrom = j - 1;
                }

                if (runIndex >= 0 && runValue - j * _gapPenalty > bestPrev)
                {
                    bestPrev = runValue - j * _gapPenalty;
                    bestFrom = runIndex;
                }

                if (bestFrom >= 0)
                {
                    best[i][j] = bestPrev + point;
                    from[i][j] = bestFrom;
                }
            }
        }

        int bestEnd = -1;
        int bestScore = _noScore;
        for (int j = 0; j < n; j++)
        {
            if (best[m - 1][j] <= _noScore)
            {
                continue;
            }

            // Length penalty: shorter candidates win ties in matched quality.
            int score = best[m - 1][j] - n;
            if (score > bestScore)
            {
                bestScore = score;
                bestEnd = j;
            }
        }

        if (bestEnd < 0)
        {
            return null;
        }

        var positions = new int[m];
        int cursor = bestEnd;
        for (int i = m - 1; i >= 0; i--)
        {
            positions[i] = cursor;
            if (i > 0)
            {
                cursor = from[i][cursor];
            }
        }

        return new FuzzyResult(bestScore, positions);
    }

    private static bool SameChar(char a, char b) =>
        char.ToUpperInvariant(a) == char.ToUpperInvariant(b);

    private static bool IsWordStart(string text, int index)
    {
        if (index == 0)
        {
            return true;
        }

        char previous = text[index - 1];
        char current = text[index];
        return !char.IsLetterOrDigit(previous)
            || (char.IsUpper(current) && char.IsLower(previous));
    }
}
