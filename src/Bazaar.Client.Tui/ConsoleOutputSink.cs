namespace Bazaar.Client.Tui;

/// <summary>
/// <see cref="ITuiOutputSink"/> over <see cref="ITerminal"/> (doc 58 work package 3): the plain line-and-bar output the installer has
/// always had, with the kind written as a word and a status bar made of text characters.
/// </summary>
public sealed class ConsoleOutputSink(ITerminal terminal, TuiStyle style) : ITuiOutputSink
{
    private const int _maxBarWidth = 24;

    public void WriteMessage(string line, TuiMessageKind kind = TuiMessageKind.Info)
    {
        ArgumentNullException.ThrowIfNull(line);

        // The label is text, so colour is never the only signal.
        terminal.WriteLine(kind switch
        {
            TuiMessageKind.Error => style.Error("Error: " + line),
            TuiMessageKind.Warning => style.Warning("Warning: " + line),
            _ => line,
        });
    }

    public void ShowProgress(TuiProgress progress)
    {
        if (progress.Total <= 0)
        {
            return;
        }

        var width = Math.Min(_maxBarWidth, Math.Max(8, terminal.Width / 4));
        terminal.WriteLine();
        terminal.WriteLine(style.Bold(ProgressText.Line(progress, width)));
    }

    public void WriteReport(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        foreach (var line in lines)
        {
            terminal.WriteLine(line);
        }
    }
}
