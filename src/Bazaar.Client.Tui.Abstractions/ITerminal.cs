namespace Bazaar.Client.Tui;

/// <summary>What kind of key was pressed. A printable key is <see cref="Character"/>, with its character in <see cref="TerminalKey.Value"/>.</summary>
public enum TerminalKeyKind
{
    Character,
    Enter,
    Backspace,
    Up,
    Down,
    Left,
    Right,
    Tab,
    Escape,

    /// <summary>The cancel chord (Ctrl+C). The wizard treats it as "stop before anything is written".</summary>
    Cancel,

    /// <summary>The input has ended (a closed pipe or Ctrl+D).</summary>
    EndOfInput,
}

/// <summary>One key press. <see cref="Value"/> is meaningful only for <see cref="TerminalKeyKind.Character"/>.</summary>
public readonly record struct TerminalKey(TerminalKeyKind Kind, char Value = '\0')
{
    public static TerminalKey Of(char value) => new(TerminalKeyKind.Character, value);
}

/// <summary>
/// The terminal as the TUI sees it (doc 58). A thin console port, so every prompt and widget runs headless in tests and the
/// terminal library (Terminal.Gui or another) stays behind it. <see cref="ReadKey"/> never echoes. <see cref="Write"/> is the only way
/// out, so a test sees everything that reaches the screen.
/// </summary>
public interface ITerminal
{
    /// <summary>True when input and output are a real terminal that can read single keys without echo. False for pipes and scripts.</summary>
    bool SupportsKeys { get; }

    /// <summary>The width in columns. Never below 40; 80x24 is the design floor of doc 58.</summary>
    int Width { get; }

    void Write(string text);

    void WriteLine(string text = "");

    /// <summary>Reads one line, echoed by the terminal as usual. Null when the input has ended.</summary>
    string? ReadLine();

    /// <summary>Reads one key without echo. Returns <see cref="TerminalKeyKind.EndOfInput"/> when the input has ended.</summary>
    TerminalKey ReadKey();

    /// <summary>Moves up over, and erases, the last <paramref name="count"/> lines, so a list can redraw in place. A no-op when keys are not supported.</summary>
    void EraseLinesAbove(int count);
}
