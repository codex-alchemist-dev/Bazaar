using System.Text;

namespace Bazaar.Server.DevSandbox.Windows;

/// <summary>
/// Builds a Windows command line from an argument vector, with the quoting rules that <c>CommandLineToArgvW</c> and the C runtime
/// use to split it again. Pure; nothing is passed through a shell. An argument with a NUL character is refused.
/// </summary>
internal static class WindowsCommandLine
{
    private static readonly char[] _needsQuotes = [' ', '\t', '\n', '\v', '"'];

    /// <summary>Quotes one argument (not the program name) so that it parses back to exactly the same string.</summary>
    internal static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("An argument must not contain a NUL character.", nameof(argument));
        }

        if (argument.Length > 0 && argument.IndexOfAny(_needsQuotes) < 0)
        {
            return argument;
        }

        var sb = new StringBuilder(argument.Length + 2);
        sb.Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
            }
            else if (c == '"')
            {
                // Backslashes before a quote are doubled, and the quote itself is escaped.
                sb.Append('\\', (backslashes * 2) + 1).Append('"');
                backslashes = 0;
            }
            else
            {
                sb.Append('\\', backslashes).Append(c);
                backslashes = 0;
            }
        }

        // Trailing backslashes sit before the closing quote, so they are doubled.
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Builds <c>"program" arg1 arg2 ...</c>. The program name is always quoted and follows the simpler argv[0] rule (no escapes), so it
    /// must not contain a quote or NUL.
    /// </summary>
    internal static string Build(string executable, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        if (executable.Contains('"', StringComparison.Ordinal) || executable.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("The executable path must not contain a quote or NUL character.", nameof(executable));
        }

        var sb = new StringBuilder();
        sb.Append('"').Append(executable).Append('"');
        foreach (var a in arguments)
        {
            sb.Append(' ').Append(Quote(a));
        }

        return sb.ToString();
    }
}
