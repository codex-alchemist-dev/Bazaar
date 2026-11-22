namespace Bazaar.Client.Tui.Tests;

public sealed class TuiModeSelectorTests
{
    private static TerminalEnvironment Env(
        bool inputRedirected = false,
        bool outputRedirected = false,
        string? noColor = null,
        string? term = "xterm-256color",
        params string[] arguments) => new(inputRedirected, outputRedirected, noColor, term, arguments);

    [Fact]
    public void A_real_terminal_with_no_flags_gets_the_full_screen_mode()
    {
        var decision = TuiModeSelector.Select(Env());

        Assert.Equal(TuiMode.FullScreen, decision.Mode);
        Assert.Equal(PlainReason.None, decision.Reason);
    }

    [Fact]
    public void Redirected_output_falls_back_to_the_plain_wizard()
    {
        Assert.Equal(new TuiModeDecision(TuiMode.Plain, PlainReason.OutputRedirected), TuiModeSelector.Select(Env(outputRedirected: true)));
    }

    [Fact]
    public void Redirected_input_falls_back_to_the_plain_wizard()
    {
        Assert.Equal(new TuiModeDecision(TuiMode.Plain, PlainReason.InputRedirected), TuiModeSelector.Select(Env(inputRedirected: true)));
    }

    [Fact]
    public void The_plain_flag_wins_even_on_a_real_terminal()
    {
        Assert.Equal(PlainReason.PlainFlag, TuiModeSelector.Select(Env(arguments: "--plain")).Reason);
    }

    [Fact]
    public void A_screen_reader_flag_forces_the_plain_wizard()
    {
        Assert.Equal(PlainReason.ScreenReaderFlag, TuiModeSelector.Select(Env(arguments: "--screen-reader")).Reason);
    }

    [Fact]
    public void A_non_empty_NO_COLOR_forces_the_plain_wizard_and_an_empty_one_does_not()
    {
        Assert.Equal(PlainReason.NoColor, TuiModeSelector.Select(Env(noColor: "1")).Reason);
        Assert.Equal(TuiMode.FullScreen, TuiModeSelector.Select(Env(noColor: "")).Mode);
    }

    [Fact]
    public void A_dumb_terminal_gets_the_plain_wizard()
    {
        Assert.Equal(PlainReason.DumbTerminal, TuiModeSelector.Select(Env(term: "dumb")).Reason);
    }

    [Fact]
    public void A_flag_is_matched_exactly()
    {
        Assert.Equal(TuiMode.FullScreen, TuiModeSelector.Select(Env(arguments: "--plainly")).Mode);
    }
}
