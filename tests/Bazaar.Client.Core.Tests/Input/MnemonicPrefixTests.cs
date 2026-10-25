using Bazaar.Client.Core.Input;

namespace Bazaar.Client.Core.Tests.Input;

public class MnemonicPrefixTests
{
    [Fact]
    public void A_prefix_mnemonic_and_its_longer_form_both_resolve_exactly()
    {
        var table = InputTestData.Table();

        Assert.Equal("window.menu", table.Exact("w")!.CommandId);
        Assert.Equal("focus.chat", table.Exact("wc")!.CommandId);
        Assert.Equal("focus.ide", table.Exact("wi")!.CommandId);
    }

    [Fact]
    public void Candidates_for_a_prefix_include_the_prefix_and_every_longer_mnemonic()
    {
        var table = InputTestData.Table();

        Assert.Equal(["w", "wc", "wi"], table.CandidatesFor("w").Select(binding => binding.Mnemonic));
        Assert.Equal(["wc"], table.CandidatesFor("wc").Select(binding => binding.Mnemonic));
    }

    [Fact]
    public void Exact_does_not_match_a_partial_mnemonic()
    {
        var table = InputTestData.Table();

        Assert.Null(table.Exact("wx"));
        Assert.Null(table.Exact("g"));
    }

    [Fact]
    public void Prefix_matching_is_case_sensitive()
    {
        var table = InputTestData.Table();

        Assert.Empty(table.CandidatesFor("W"));
        Assert.Null(table.Exact("WC"));
    }

    [Fact]
    public void The_palette_mnemonic_is_registered_as_the_palette_command()
    {
        var table = InputTestData.Table();

        Assert.Equal("  ", table.PaletteMnemonic);
        Assert.Equal(MnemonicTable.PaletteCommandId, table.Exact("  ")!.CommandId);
    }

    [Fact]
    public void Two_commands_on_one_mnemonic_fail_the_load()
    {
        var result = MnemonicTable.Create(
            [InputTestData.Space, InputTestData.Space],
            [new MnemonicBinding("wc", "a"), new MnemonicBinding("wc", "b")]);

        Assert.Null(result.Table);
        Assert.Contains(result.Errors, error => error.Contains("'wc'", StringComparison.Ordinal) && error.Contains("more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void A_command_cannot_reuse_the_palette_mnemonic()
    {
        var result = MnemonicTable.Create(
            [InputTestData.Space, InputTestData.Space],
            [new MnemonicBinding("  ", "window.menu")]);

        Assert.Null(result.Table);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void A_mnemonic_starting_with_the_reserved_insert_key_fails_the_load()
    {
        var result = MnemonicTable.Create(
            [InputTestData.Space, InputTestData.Space],
            [new MnemonicBinding("ide", "focus.ide")]);

        Assert.Null(result.Table);
        Assert.Contains(result.Errors, error => error.Contains("reserved", StringComparison.Ordinal));
    }

    [Fact]
    public void A_palette_mnemonic_with_ctrl_held_fails_the_load()
    {
        var result = MnemonicTable.Create(
            [KeyStroke.OfChar('k', KeyModifiers.Control)],
            []);

        Assert.Null(result.Table);
        Assert.Contains(result.Errors, error => error.Contains("Ctrl", StringComparison.Ordinal));
    }

    [Fact]
    public void A_command_mnemonic_with_punctuation_fails_the_load()
    {
        var result = MnemonicTable.Create(
            [InputTestData.Space, InputTestData.Space],
            [new MnemonicBinding("g-c", "gc.open")]);

        Assert.Null(result.Table);
        Assert.NotEmpty(result.Errors);
    }
}
