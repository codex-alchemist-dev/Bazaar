namespace Bazaar.Client.Terminal.Calcinato;

/// <summary>A console size in character cells.</summary>
/// <param name="Columns">Columns, at least 1.</param>
/// <param name="Rows">Rows, at least 1.</param>
public readonly record struct ConsoleSize(int Columns, int Rows);

/// <summary>
/// The console the Calcinato host draws to and reads keys from. The real console is <see cref="SystemConsoleSurface"/>;
/// tests use a fake, so the host runs without a display.
/// </summary>
public interface IConsoleSurface
{
    /// <summary>Blocks until the next key press. The host calls it from one dedicated background thread.</summary>
    ConsoleKeyInfo ReadKey();

    /// <summary>The current console size.</summary>
    ConsoleSize WindowSize { get; }

    /// <summary>When true, Ctrl+C arrives as a key instead of stopping the process.</summary>
    bool TreatControlCAsInput { get; set; }

    /// <summary>Writes raw text, including ANSI control sequences.</summary>
    /// <param name="text">The text to write.</param>
    void Write(string text);
}

/// <summary>The process console. Output goes to <see cref="Console.Out"/>; input is read with <see cref="Console.ReadKey(bool)"/>.</summary>
public sealed class SystemConsoleSurface : IConsoleSurface
{
    private const int _fallbackColumns = 80;
    private const int _fallbackRows = 24;

    /// <inheritdoc />
    public ConsoleKeyInfo ReadKey() => Console.ReadKey(intercept: true);

    /// <inheritdoc />
    public ConsoleSize WindowSize
    {
        get
        {
            try
            {
                return new ConsoleSize(Math.Max(1, Console.WindowWidth), Math.Max(1, Console.WindowHeight));
            }
            catch (IOException)
            {
                return new ConsoleSize(_fallbackColumns, _fallbackRows);
            }
        }
    }

    /// <inheritdoc />
    public bool TreatControlCAsInput
    {
        get => Console.TreatControlCAsInput;
        set => Console.TreatControlCAsInput = value;
    }

    /// <inheritdoc />
    public void Write(string text) => Console.Out.Write(text);
}
