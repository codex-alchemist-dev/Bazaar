namespace Bazaar.Client.Tui;

/// <summary>
/// The real console behind <see cref="ITerminal"/>. It is the only place the TUI kit touches <see cref="Console"/>. Keys are read with
/// <c>intercept: true</c>, so nothing typed is echoed.
/// </summary>
public sealed class SystemTerminal : ITerminal
{
    private const int _minimumWidth = 40;
    private const int _fallbackWidth = 80;

    public bool SupportsKeys => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public int Width
    {
        get
        {
            try
            {
                return Math.Max(_minimumWidth, Console.WindowWidth);
            }
            catch (IOException)
            {
                return _fallbackWidth;
            }
        }
    }

    public void Write(string text) => Console.Out.Write(text);

    public void WriteLine(string text = "") => Console.Out.WriteLine(text);

    public string? ReadLine() => Console.In.ReadLine();

    public TerminalKey ReadKey()
    {
        if (Console.IsInputRedirected)
        {
            var next = Console.In.Read();
            return next < 0 ? new TerminalKey(TerminalKeyKind.EndOfInput) : Translate((char)next);
        }

        var info = Console.ReadKey(intercept: true);
        if (info.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            return info.Key switch
            {
                ConsoleKey.C => new TerminalKey(TerminalKeyKind.Cancel),
                ConsoleKey.D => new TerminalKey(TerminalKeyKind.EndOfInput),
                _ => default,
            };
        }

        return info.Key switch
        {
            ConsoleKey.Enter => new TerminalKey(TerminalKeyKind.Enter),
            ConsoleKey.Backspace => new TerminalKey(TerminalKeyKind.Backspace),
            ConsoleKey.UpArrow => new TerminalKey(TerminalKeyKind.Up),
            ConsoleKey.DownArrow => new TerminalKey(TerminalKeyKind.Down),
            ConsoleKey.LeftArrow => new TerminalKey(TerminalKeyKind.Left),
            ConsoleKey.RightArrow => new TerminalKey(TerminalKeyKind.Right),
            ConsoleKey.Tab => new TerminalKey(TerminalKeyKind.Tab),
            ConsoleKey.Escape => new TerminalKey(TerminalKeyKind.Escape),
            _ => char.IsControl(info.KeyChar) ? default : TerminalKey.Of(info.KeyChar),
        };
    }

    public void EraseLinesAbove(int count)
    {
        if (!SupportsKeys)
        {
            return;
        }

        for (var line = 0; line < count; line++)
        {
            Console.Out.Write("\u001b[1A\u001b[2K");
        }

        Console.Out.Write('\r');
    }

    private static TerminalKey Translate(char value) => value switch
    {
        '\n' or '\r' => new TerminalKey(TerminalKeyKind.Enter),
        '\u0003' => new TerminalKey(TerminalKeyKind.Cancel),
        _ => TerminalKey.Of(value),
    };
}
