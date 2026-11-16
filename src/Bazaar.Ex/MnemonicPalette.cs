namespace Bazaar.Ex;

/// <summary>
/// Fuzzy filter for the palette's mnemonic list. Pure and head-neutral: the view hands in the rows it shows and the typed query.
/// An empty query returns every row in listing order. Otherwise rows rank by how the query matches: a mnemonic prefix first,
/// then a title prefix, then a subsequence match over the mnemonic and title. Ties keep listing order, so results are stable.
/// </summary>
public static class MnemonicPalette
{
    private const int _mnemonicPrefixRank = 0;
    private const int _titlePrefixRank = 1;
    private const int _subsequenceRank = 2;

    public static IReadOnlyList<MnemonicListRow> Filter(IEnumerable<MnemonicListRow> rows, string query)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(query);

        var ordered = rows.ToList();
        var needle = query.Trim();
        if (needle.Length == 0)
        {
            return ordered;
        }

        return ordered
            .Select((row, index) => (row, index, rank: Rank(row, needle)))
            .Where(item => item.rank is not null)
            .OrderBy(item => item.rank)
            .ThenBy(item => item.index)
            .Select(item => item.row)
            .ToList();
    }

    private static int? Rank(MnemonicListRow row, string needle)
    {
        if (row.Mnemonic.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
        {
            return _mnemonicPrefixRank;
        }

        if (row.Title.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
        {
            return _titlePrefixRank;
        }

        var haystack = row.Mnemonic + " " + row.Title;
        return IsSubsequence(needle, haystack) ? _subsequenceRank : null;
    }

    private static bool IsSubsequence(string needle, string haystack)
    {
        var position = 0;
        foreach (var character in needle)
        {
            var found = haystack.IndexOf(character, position);
            if (found < 0 && !char.IsLetterOrDigit(character))
            {
                continue;
            }

            if (found < 0)
            {
                return false;
            }

            position = found + 1;
        }

        return true;
    }
}
