using System.Globalization;

namespace Bazaar.Client.Calcinato.Modal;

/// <summary>Every ex command the command line accepts (doc 52, section 5.2).</summary>
public enum ExCommandKind
{
    /// <summary><c>:q</c> close the terminal.</summary>
    Quit,

    /// <summary><c>:q!</c> close and kill the process.</summary>
    QuitForce,

    /// <summary><c>:new [shell]</c>.</summary>
    New,

    /// <summary><c>:vsplit</c>.</summary>
    VSplit,

    /// <summary><c>:split</c>.</summary>
    Split,

    /// <summary><c>:attach &lt;terminalId&gt;</c>.</summary>
    Attach,

    /// <summary><c>:detach</c>.</summary>
    Detach,

    /// <summary><c>:take</c> take control of a server terminal.</summary>
    Take,

    /// <summary><c>:release</c> give control back.</summary>
    Release,

    /// <summary><c>:set &lt;option&gt;[=value]</c>.</summary>
    Set,

    /// <summary><c>:crash list</c>.</summary>
    CrashList,

    /// <summary><c>:crash preview N</c>.</summary>
    CrashPreview,

    /// <summary><c>:crash send N</c>.</summary>
    CrashSend,

    /// <summary><c>:crash drop N</c>.</summary>
    CrashDrop,

    /// <summary><c>:interrupt</c> sends 0x03 to the shell. The engine handles it, not the host.</summary>
    Interrupt,

    /// <summary><c>:eof</c> sends 0x04 to the shell. The engine handles it, not the host.</summary>
    Eof,

    /// <summary><c>:suspend</c> sends 0x1A to the shell. The engine handles it, not the host.</summary>
    Suspend,

    /// <summary><c>:clear</c>.</summary>
    Clear,

    /// <summary><c>:theme [name]</c>.</summary>
    Theme,

    /// <summary><c>:help [topic]</c>.</summary>
    Help,
}

/// <summary>A parsed ex command.</summary>
/// <param name="Kind">Which command.</param>
/// <param name="Argument">The argument text: a shell for <c>new</c>, a terminal id for <c>attach</c>, the option name for <c>set</c>, and so on. Null when absent.</param>
/// <param name="Value">The value after <c>=</c> for <c>set</c>. Null when there was no <c>=</c>.</param>
/// <param name="Number">The crash report number for the <c>crash</c> subcommands.</param>
public sealed record ExCommand(ExCommandKind Kind, string? Argument = null, string? Value = null, int? Number = null);

/// <summary>The outcome of parsing one ex line: a command, or an error message.</summary>
/// <param name="Command">The command when parsing succeeded.</param>
/// <param name="Error">The error text when parsing failed.</param>
public sealed record ExParseResult(ExCommand? Command, string? Error)
{
    /// <summary>True when <see cref="Command"/> holds a command.</summary>
    public bool Succeeded => Command is not null;
}

/// <summary>
/// Parses ex command lines. Names are case-sensitive and must be exact (no abbreviations). Unknown names, extra
/// arguments and malformed arguments are errors and never produce a command.
/// </summary>
public static class ExCommandParser
{
    private static readonly char[] SplitChars = { ' ', '\t' };

    /// <summary>Parses one line, without the leading colon.</summary>
    /// <param name="input">The typed text.</param>
    public static ExParseResult Parse(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        string text = input.Trim();
        if (text.Length == 0)
        {
            return Fail("No command given.");
        }

        int split = IndexOfWhitespace(text);
        string name = split < 0 ? text : text[..split];
        string rest = split < 0 ? string.Empty : text[split..].Trim();

        switch (name)
        {
            case "q":
                return NoArguments(name, rest, ExCommandKind.Quit);
            case "q!":
                return NoArguments(name, rest, ExCommandKind.QuitForce);
            case "vsplit":
                return NoArguments(name, rest, ExCommandKind.VSplit);
            case "split":
                return NoArguments(name, rest, ExCommandKind.Split);
            case "detach":
                return NoArguments(name, rest, ExCommandKind.Detach);
            case "take":
                return NoArguments(name, rest, ExCommandKind.Take);
            case "release":
                return NoArguments(name, rest, ExCommandKind.Release);
            case "clear":
                return NoArguments(name, rest, ExCommandKind.Clear);
            case "interrupt":
                return NoArguments(name, rest, ExCommandKind.Interrupt);
            case "eof":
                return NoArguments(name, rest, ExCommandKind.Eof);
            case "suspend":
                return NoArguments(name, rest, ExCommandKind.Suspend);
            case "new":
                return Ok(new ExCommand(ExCommandKind.New, NullIfEmpty(rest)));
            case "theme":
                return Ok(new ExCommand(ExCommandKind.Theme, NullIfEmpty(rest)));
            case "help":
                return Ok(new ExCommand(ExCommandKind.Help, NullIfEmpty(rest)));
            case "attach":
                return ParseAttach(rest);
            case "set":
                return ParseSet(rest);
            case "crash":
                return ParseCrash(rest);
            default:
                return Fail($"E492: Not an editor command: {name}");
        }
    }

    private static ExParseResult ParseAttach(string rest)
    {
        if (rest.Length == 0)
        {
            return Fail("attach needs a terminal id.");
        }

        if (IndexOfWhitespace(rest) >= 0)
        {
            return Fail("attach takes one terminal id.");
        }

        return Ok(new ExCommand(ExCommandKind.Attach, rest));
    }

    private static ExParseResult ParseSet(string rest)
    {
        if (rest.Length == 0)
        {
            return Fail("set needs an option name, for example set scrollback=5000.");
        }

        int equals = rest.IndexOf('=', StringComparison.Ordinal);
        string option = equals < 0 ? rest : rest[..equals];
        string? value = equals < 0 ? null : rest[(equals + 1)..];
        if (!IsOptionName(option))
        {
            return Fail($"Invalid option name: {option}");
        }

        return Ok(new ExCommand(ExCommandKind.Set, option, value));
    }

    private static ExParseResult ParseCrash(string rest)
    {
        string[] tokens = rest.Split(SplitChars, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return Fail("crash needs list, preview N, send N or drop N.");
        }

        string sub = tokens[0];
        if (sub == "list")
        {
            return tokens.Length == 1 ? Ok(new ExCommand(ExCommandKind.CrashList)) : Fail("crash list takes no number.");
        }

        if (sub is not ("preview" or "send" or "drop"))
        {
            return Fail($"Unknown crash subcommand: {sub}");
        }

        ExCommandKind kind = sub switch
        {
            "preview" => ExCommandKind.CrashPreview,
            "send" => ExCommandKind.CrashSend,
            _ => ExCommandKind.CrashDrop,
        };

        if (tokens.Length != 2)
        {
            return Fail($"crash {sub} needs one crash number.");
        }

        if (!int.TryParse(tokens[1], NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 1)
        {
            return Fail("The crash number must be a positive whole number.");
        }

        return Ok(new ExCommand(kind, Number: number));
    }

    private static ExParseResult NoArguments(string name, string rest, ExCommandKind kind) =>
        rest.Length == 0 ? Ok(new ExCommand(kind)) : Fail($"{name} takes no arguments.");

    private static bool IsOptionName(string name)
    {
        if (name.Length == 0 || name[0] is < 'a' or > 'z')
        {
            return false;
        }

        foreach (char c in name)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static int IndexOfWhitespace(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;

    private static ExParseResult Ok(ExCommand command) => new(command, null);

    private static ExParseResult Fail(string message) => new(null, message);
}
