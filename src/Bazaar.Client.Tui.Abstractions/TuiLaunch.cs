namespace Bazaar.Client.Tui;

/// <summary>How a TUI program runs (doc 58 section 4).</summary>
public enum TuiMode
{
    /// <summary>The full-screen interface: menu bar, widgets, status line.</summary>
    FullScreen,

    /// <summary>The plain, one-question-per-line path. It is the accessible and scripted path and stays beside the TUI.</summary>
    Plain,
}

/// <summary>Why <see cref="TuiMode.Plain"/> was chosen. <see cref="None"/> when the full-screen mode was.</summary>
public enum PlainReason
{
    None,
    PlainFlag,
    ScreenReaderFlag,
    InputRedirected,
    OutputRedirected,
    NoColor,
    DumbTerminal,
}

/// <summary>The facts the mode choice reads, gathered once by the host so the choice itself is a pure function.</summary>
public sealed record TerminalEnvironment(
    bool InputRedirected,
    bool OutputRedirected,
    string? NoColor,
    string? Term,
    IReadOnlyList<string> Arguments);

/// <summary>The mode, and the reason when it is <see cref="TuiMode.Plain"/>.</summary>
public sealed record TuiModeDecision(TuiMode Mode, PlainReason Reason);
