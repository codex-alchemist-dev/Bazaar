namespace Bazaar.Guest.PoolSpawn;

/// <summary>Why the command line was refused. No value carries an argument's text, so a refusal cannot echo a secret.</summary>
public enum SpawnParseError
{
    None,
    UnknownOption,
    DuplicateOption,
    MissingValue,
    MissingOption,
    BadPath,
    SamePath,
    MissingSeparator,
    MissingProgram,
    BadProgram,
}

/// <summary>
/// The parsed command line of <c>bazaar-pool-spawn --fd3 PATH --fd4 PATH -- PROGRAM ARGS...</c>. The parser is pure and strict: every
/// path is absolute with no <c>..</c>, each option appears once, and anything it does not know is an error.
/// </summary>
internal sealed record SpawnArguments(string Fd3Path, string Fd4Path, string Program, IReadOnlyList<string> Argv)
{
    /// <summary>Longest path accepted (the Linux PATH_MAX is 4096 including the terminator).</summary>
    public const int MaxPathLength = 4095;

    /// <summary>The process exit code for a refused command line (EX_USAGE).</summary>
    public const int UsageExitCode = 64;

    private const string _fd3Option = "--fd3";
    private const string _fd4Option = "--fd4";
    private const string _separator = "--";

    /// <summary>True when the text is an absolute path: no control character, no empty, <c>.</c> or <c>..</c> segment, no trailing slash, not too long.</summary>
    public static bool IsAcceptablePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxPathLength || path[0] != '/' || path.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        if (path.Any(char.IsControl) || path.EndsWith('/'))
        {
            return false;
        }

        // "//" and "/./" make a path ambiguous to read; refuse them rather than normalise.
        foreach (var segment in path[1..].Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Parses the arguments. On failure <paramref name="error"/> names the reason and the result is null.</summary>
    public static bool TryParse(IReadOnlyList<string> args, out SpawnArguments? result, out SpawnParseError error)
    {
        ArgumentNullException.ThrowIfNull(args);
        result = null;
        string? fd3 = null;
        string? fd4 = null;
        var i = 0;
        for (; i < args.Count && args[i] != _separator; i += 2)
        {
            var name = args[i];
            if (name is not (_fd3Option or _fd4Option))
            {
                error = SpawnParseError.UnknownOption;
                return false;
            }

            if (i + 1 >= args.Count || args[i + 1] == _separator)
            {
                error = SpawnParseError.MissingValue;
                return false;
            }

            if ((name == _fd3Option && fd3 is not null) || (name == _fd4Option && fd4 is not null))
            {
                error = SpawnParseError.DuplicateOption;
                return false;
            }

            if (!IsAcceptablePath(args[i + 1]))
            {
                error = SpawnParseError.BadPath;
                return false;
            }

            if (name == _fd3Option)
            {
                fd3 = args[i + 1];
            }
            else
            {
                fd4 = args[i + 1];
            }
        }

        if (i >= args.Count)
        {
            error = SpawnParseError.MissingSeparator;
            return false;
        }

        if (fd3 is null || fd4 is null)
        {
            error = SpawnParseError.MissingOption;
            return false;
        }

        if (string.Equals(fd3, fd4, StringComparison.Ordinal))
        {
            error = SpawnParseError.SamePath;
            return false;
        }

        var rest = args.Skip(i + 1).ToArray();
        if (rest.Length == 0)
        {
            error = SpawnParseError.MissingProgram;
            return false;
        }

        if (!IsAcceptablePath(rest[0]))
        {
            error = SpawnParseError.BadProgram;
            return false;
        }

        result = new SpawnArguments(fd3, fd4, rest[0], rest);
        error = SpawnParseError.None;
        return true;
    }

    /// <summary>A fixed message for a refusal. It never contains argument text.</summary>
    public static string Describe(SpawnParseError error) => error switch
    {
        SpawnParseError.UnknownOption => "unknown option",
        SpawnParseError.DuplicateOption => "an option was given twice",
        SpawnParseError.MissingValue => "an option has no value",
        SpawnParseError.MissingOption => "both --fd3 and --fd4 are required",
        SpawnParseError.BadPath => "a pipe path must be absolute, without '..', '.', empty segments or control characters",
        SpawnParseError.SamePath => "--fd3 and --fd4 must be different paths",
        SpawnParseError.MissingSeparator => "the program must follow a '--' separator",
        SpawnParseError.MissingProgram => "no program was given after '--'",
        SpawnParseError.BadProgram => "the program must be an absolute path without '..'",
        _ => "invalid arguments",
    };
}
