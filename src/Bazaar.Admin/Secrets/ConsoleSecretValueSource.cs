namespace Bazaar.Admin;

/// <summary>
/// The console source. With redirected input it reads one line from standard input. On a terminal it reads keys without echo, with
/// Backspace to correct and Ctrl+C to cancel. Nothing typed is ever written to the output.
/// </summary>
internal sealed class ConsoleSecretValueSource : ISecretValueSource
{
    private const char _cancelKey = '\u0003';
    private const char _backspace = '\b';

    /// <summary>The shared instance. It holds no state.</summary>
    public static ConsoleSecretValueSource Instance { get; } = new();

    public string? Read(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        if (Console.IsInputRedirected)
        {
            return Console.In.ReadLine();
        }

        Console.Error.Write(prompt);
        var typed = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.KeyChar == _cancelKey)
            {
                Console.Error.WriteLine();
                return null;
            }

            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return new string(typed.ToArray());
            }

            if (key.KeyChar == _backspace || key.Key == ConsoleKey.Backspace)
            {
                if (typed.Count > 0)
                {
                    typed.RemoveAt(typed.Count - 1);
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                typed.Add(key.KeyChar);
            }
        }
    }
}
