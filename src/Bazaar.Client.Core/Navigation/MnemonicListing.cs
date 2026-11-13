namespace Bazaar.Client.Core.Navigation;

/// <summary>One row of the palette's mnemonic list.</summary>
public sealed record MnemonicListRow(string Mnemonic, string Title, string CommandId, MnemonicTier Tier, string Source, bool Locked, bool Alias);

/// <summary>Every effective mnemonic, sorted for the palette: navigation first, then by mnemonic text.</summary>
public static class MnemonicListing
{
    public static IReadOnlyList<MnemonicListRow> Build(IEnumerable<MnemonicAssignment> effective)
    {
        ArgumentNullException.ThrowIfNull(effective);

        return effective
            .Select(a => new MnemonicListRow(
                a.Mnemonic,
                a.Title,
                a.CommandId,
                a.Tier,
                a.Source,
                Locked: a.Tier == MnemonicTier.CompanyLocked,
                Alias: a.Alias))
            .OrderBy(row => row.Mnemonic.StartsWith(MnemonicGrammar.NavigateVerb) ? 0 : 1)
            .ThenBy(row => row.Mnemonic, StringComparer.Ordinal)
            .ToList();
    }
}
