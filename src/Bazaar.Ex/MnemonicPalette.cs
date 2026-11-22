using Bazaar.Client.Commands.Fuzzy;
using Bazaar.Localisation;

namespace Bazaar.Ex;

/// <summary>
/// Fuzzy filter for the palette's mnemonic list. Pure and head-neutral: the view hands in the rows it shows and the typed query.
/// An empty query returns every row in listing order. Otherwise rows rank by how the query matches: a mnemonic prefix first,
/// then a title prefix, then a subsequence match over the mnemonic and title. Matching folds Unicode to NFC and ignores case and
/// diacritics (doc 49 section 2), so "regl" finds "Réglages". Ties keep listing order, so results are stable.
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
        var needle = TextFold.Fold(query.Trim());
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
        var mnemonic = TextFold.Fold(row.Mnemonic);
        var title = TextFold.Fold(row.Title);
        if (mnemonic.StartsWith(needle, StringComparison.Ordinal))
        {
            return _mnemonicPrefixRank;
        }

        if (title.StartsWith(needle, StringComparison.Ordinal))
        {
            return _titlePrefixRank;
        }

        var haystack = mnemonic + " " + title;
        return FuzzyCore.IsSubsequence(needle, haystack, skipMissingSymbols: true) ? _subsequenceRank : null;
    }
}
