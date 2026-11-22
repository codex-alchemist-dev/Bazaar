namespace Bazaar.Client.Tui.Tests;

public sealed class TerminalPromptWidgetsTests
{
    private const string _canary = "CANARY-s3cret-9f2c";

    private static TerminalKey Key(TerminalKeyKind kind) => new(kind);

    [Fact]
    public void An_empty_text_entry_takes_the_default_and_a_typed_one_wins()
    {
        var terminal = new ScriptedTerminal().Lines("", "typed");
        var widgets = new TerminalPromptWidgets(terminal);

        Assert.Equal("beta", widgets.TextEntry("Channel", "beta"));
        Assert.Equal("typed", widgets.TextEntry("Channel", "beta"));
        Assert.Contains("Channel [beta]: ", terminal.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Ended_input_returns_null()
    {
        Assert.Null(new TerminalPromptWidgets(new ScriptedTerminal()).TextEntry("Name", null));
    }

    [Fact]
    public void A_line_pick_takes_a_number_a_name_or_the_default_and_asks_again_on_a_bad_entry()
    {
        var terminal = new ScriptedTerminal().Lines("nope", "2", "A", "");
        var widgets = new TerminalPromptWidgets(terminal);
        string[] options = ["a", "b", "c"];

        Assert.Equal("b", widgets.PickOne("Pick", options, "c"));
        Assert.Contains("Choose one of the options listed above.", terminal.Output, StringComparison.Ordinal);
        Assert.Equal("a", widgets.PickOne("Pick", options, "c"));
        Assert.Equal("c", widgets.PickOne("Pick", options, "c"));
    }

    [Fact]
    public void A_line_multi_pick_reads_numbers_and_an_empty_entry_means_the_defaults()
    {
        var terminal = new ScriptedTerminal().Lines("3,1", "9", "1", "");
        var widgets = new TerminalPromptWidgets(terminal);
        string[] options = ["a", "b", "c"];

        Assert.Equal(["a", "c"], widgets.PickMany("Pick", options, ["b"]));
        Assert.Equal(["a"], widgets.PickMany("Pick", options, []));
        Assert.Equal(["b"], widgets.PickMany("Pick", options, ["b"]));
        Assert.Contains("Give numbers from 1 to 3", terminal.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Yes_no_takes_the_default_and_asks_again_on_a_bad_entry()
    {
        var terminal = new ScriptedTerminal().Lines("", "maybe", "n");
        var widgets = new TerminalPromptWidgets(terminal);

        Assert.True(widgets.YesNo("Sure", defaultYes: true));
        Assert.False(widgets.YesNo("Sure", defaultYes: true));
        Assert.Contains("Answer yes or no.", terminal.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_arrow_key_list_moves_with_up_and_down_and_chooses_with_enter()
    {
        var terminal = new ScriptedTerminal(supportsKeys: true).Keys(Key(TerminalKeyKind.Down), Key(TerminalKeyKind.Down), Key(TerminalKeyKind.Up), Key(TerminalKeyKind.Enter));
        var widgets = new TerminalPromptWidgets(terminal);

        Assert.Equal("b", widgets.PickOne("Pick", ["a", "b", "c"], null));
        Assert.Equal(3 * 3, terminal.ErasedLines);
    }

    [Fact]
    public void The_arrow_key_multi_list_ticks_with_space()
    {
        var terminal = new ScriptedTerminal(supportsKeys: true).Keys(
            TerminalKey.Of(' '), Key(TerminalKeyKind.Down), Key(TerminalKeyKind.Down), TerminalKey.Of(' '), Key(TerminalKeyKind.Enter));
        var widgets = new TerminalPromptWidgets(terminal);

        Assert.Equal(["a", "c"], widgets.PickMany("Pick", ["a", "b", "c"], []));
        Assert.Contains("[x] a", terminal.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Cancel_in_a_key_list_returns_null()
    {
        var terminal = new ScriptedTerminal(supportsKeys: true).Keys(Key(TerminalKeyKind.Cancel));

        Assert.Null(new TerminalPromptWidgets(terminal).PickOne("Pick", ["a", "b"], null));
    }

    [Fact]
    public void A_secret_typed_by_key_is_not_echoed_and_backspace_corrects_it()
    {
        var terminal = new ScriptedTerminal(supportsKeys: true).Typed(_canary + "x");
        var widgets = new TerminalPromptWidgets(terminal);

        var secret = widgets.SecretEntry("Key");

        Assert.Equal(_canary + "x", secret);
        Assert.DoesNotContain(_canary, terminal.Output, StringComparison.Ordinal);
        Assert.Equal("Key: \n", terminal.Output);
    }

    [Fact]
    public void Backspace_removes_the_last_secret_character_without_drawing_anything()
    {
        var terminal = new ScriptedTerminal(supportsKeys: true).Keys(
            TerminalKey.Of('a'), TerminalKey.Of('b'), Key(TerminalKeyKind.Backspace), TerminalKey.Of('c'), Key(TerminalKeyKind.Enter));

        Assert.Equal("ac", new TerminalPromptWidgets(terminal).SecretEntry("Key"));
        Assert.Equal("Key: \n", terminal.Output);
    }

    [Fact]
    public void A_cancelled_secret_returns_null()
    {
        var terminal = new ScriptedTerminal(supportsKeys: true).Keys(TerminalKey.Of('a'), Key(TerminalKeyKind.Cancel));

        Assert.Null(new TerminalPromptWidgets(terminal).SecretEntry("Key"));
    }

    [Fact]
    public void A_piped_secret_is_read_as_one_line_and_not_written_back()
    {
        var terminal = new ScriptedTerminal().Lines(_canary);

        Assert.Equal(_canary, new TerminalPromptWidgets(terminal).SecretEntry("Key"));
        Assert.DoesNotContain(_canary, terminal.Output, StringComparison.Ordinal);
    }
}
