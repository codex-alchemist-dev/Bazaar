namespace Bazaar.Client.Tui;

/// <summary>How serious a line shown to the operator is. A sink always writes the kind as a word, so colour is never the only signal (doc 58 section 4).</summary>
public enum TuiMessageKind
{
    Info,
    Warning,
    Error,
}

/// <summary>Where a long task is: step <see cref="Current"/> of <see cref="Total"/> (counting from 1), and what the step is called.</summary>
public readonly record struct TuiProgress(int Current, int Total, string Title);

/// <summary>
/// The output port (doc 58 work package 3): everything a wizard or screen shows that is not a question. The console sink writes
/// through <see cref="ITerminal"/> and keeps the plain path as it was; the Terminal.Gui sink draws into a scrolling log and a
/// progress line, because writing to the console while Terminal.Gui owns the screen would corrupt it. A sink only ever receives text
/// the caller chose to show: a secret has no way in, and a sink never logs or echoes anything it was not given.
/// </summary>
public interface ITuiOutputSink
{
    /// <summary>Shows one line. An empty line is a blank row.</summary>
    void WriteMessage(string line, TuiMessageKind kind = TuiMessageKind.Info);

    /// <summary>Shows where the task is.</summary>
    void ShowProgress(TuiProgress progress);

    /// <summary>Shows a block of report lines, in order, as one unit.</summary>
    void WriteReport(IReadOnlyList<string> lines);
}
