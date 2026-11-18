using Bazaar.Ex;

namespace Bazaar.Ex.Tests;

public class MnemonicPaletteTests
{
    private static readonly IReadOnlyList<MnemonicListRow> _rows =
        MnemonicListing.Build(MnemonicResolver.Resolve(NavigationDefaults.Assignments).Effective);

    [Fact]
    public void Empty_query_returns_every_row_in_listing_order()
    {
        var result = MnemonicPalette.Filter(_rows, "   ");

        Assert.Equal(_rows, result);
    }

    [Fact]
    public void Mnemonic_prefix_ranks_above_title_matches()
    {
        var result = MnemonicPalette.Filter(_rows, "n");

        Assert.Equal("ngc1", result[0].Mnemonic);
        Assert.StartsWith("n", result[1].Mnemonic);
        Assert.DoesNotContain(result.Skip(2), row => row.Mnemonic.StartsWith('n'));
    }

    [Fact]
    public void Title_prefix_is_found_without_typing_the_mnemonic()
    {
        var result = MnemonicPalette.Filter(_rows, "RyuseiFuda");

        Assert.Contains(result, row => row.CommandId == NavigationDefaults.RyuseiBoardCommandId);
    }

    [Fact]
    public void Subsequence_matches_across_the_title()
    {
        var result = MnemonicPalette.Filter(_rows, "rdmp");

        Assert.Contains(result, row => row.CommandId == NavigationDefaults.RyuseiRoadmapCommandId);
    }

    [Fact]
    public void Query_with_no_match_returns_nothing()
    {
        var result = MnemonicPalette.Filter(_rows, "zzzqqq");

        Assert.Empty(result);
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        var lower = MnemonicPalette.Filter(_rows, "settings");
        var upper = MnemonicPalette.Filter(_rows, "SETTINGS");

        Assert.Equal(lower, upper);
        Assert.NotEmpty(lower);
    }
}
