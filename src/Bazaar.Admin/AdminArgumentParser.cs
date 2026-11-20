namespace Bazaar.Admin;

/// <summary>What the command was asked to do.</summary>
public enum AdminAction
{
    Help,
    Validate,
    DryRun,
    Apply,
}

/// <summary>The parsed command line. <see cref="ConfigPath"/> is set for every action except help.</summary>
public sealed record ParsedArguments(AdminAction Action, string? ConfigPath, string? PlanHash, bool HelpForSetup);

/// <summary>
/// An argument error. <see cref="Flag"/> names the flag or the position, and <see cref="Exception.Message"/> names the rule. Neither
/// repeats an argument's text, so a mistyped secret cannot reach the output.
/// </summary>
public sealed class AdminUsageException(string flag, string message) : Exception(message)
{
    public string Flag { get; } = flag;
}

/// <summary>
/// The hand-written parser (no package). It accepts <c>--name value</c> and <c>--name=value</c>. An unknown option is named only
/// when it has the shape of an option (letters and dashes); any other text is not echoed.
/// </summary>
public static class AdminArgumentParser
{
    private const string _configFlag = "--config";
    private const string _planHashFlag = "--plan-hash";
    private const string _dryRunFlag = "--dry-run";
    private const string _jsonFlag = "--json";
    private const string _validateWord = "validate";
    private const string _applyWord = "apply";
    private const int _planHashLength = 64;

    /// <summary>Parses the arguments, or throws <see cref="AdminUsageException"/>.</summary>
    public static ParsedArguments Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count == 0)
        {
            throw new AdminUsageException("command", $"no command given. Run {AdminConstants.CommandName} --help.");
        }

        if (args.Any(IsHelpFlag) || args[0] == "help")
        {
            return new ParsedArguments(AdminAction.Help, null, null, HelpForSetup: args[0] == AdminConstants.SetupCommandWord);
        }

        if (args[0] != AdminConstants.SetupCommandWord)
        {
            throw new AdminUsageException("command", $"unknown command. The only command is {AdminConstants.SetupCommandWord}. Run {AdminConstants.CommandName} --help.");
        }

        var index = 1;
        string? subcommand = null;
        if (index < args.Count && !args[index].StartsWith('-'))
        {
            subcommand = args[index];
            index++;
            if (subcommand is not (_validateWord or _applyWord))
            {
                throw new AdminUsageException("command", "unknown setup command. Use validate or apply, or --dry-run with --config.");
            }
        }

        string? config = null;
        string? planHash = null;
        var dryRun = false;
        for (; index < args.Count; index++)
        {
            var token = args[index];
            var equals = token.IndexOf('=', StringComparison.Ordinal);
            var name = equals >= 0 ? token[..equals] : token;
            var inline = equals >= 0 ? token[(equals + 1)..] : null;

            switch (name)
            {
                case _configFlag:
                    config = ValueOf(args, ref index, name, inline);
                    break;
                case _planHashFlag:
                    planHash = ValueOf(args, ref index, name, inline);
                    break;
                case _jsonFlag:
                    RejectInline(name, inline);
                    break;
                case _dryRunFlag:
                    RejectInline(name, inline);
                    dryRun = true;
                    break;
                default:
                    throw UnknownToken(name);
            }
        }

        // Each flag belongs to the forms that use it. The checks run before the file is read.
        if (subcommand is null)
        {
            if (!dryRun)
            {
                throw new AdminUsageException(AdminConstants.SetupCommandWord, $"needs {_validateWord}, {_applyWord}, or {_dryRunFlag}. Run {AdminConstants.CommandName} setup --help.");
            }

            RejectFlag(planHash is not null, _planHashFlag, "is only for apply.");
            return Build(AdminAction.DryRun, config, null);
        }

        if (subcommand == _validateWord)
        {
            RejectFlag(dryRun, _dryRunFlag, "is not used with validate. Use setup --dry-run.");
            RejectFlag(planHash is not null, _planHashFlag, "is only for apply.");
            return Build(AdminAction.Validate, config, null);
        }

        RejectFlag(dryRun, _dryRunFlag, "is not used with apply.");
        if (planHash is null)
        {
            throw new AdminUsageException(_planHashFlag, "is required for apply. Use the hash that the dry run printed.");
        }

        if (!IsPlanHash(planHash))
        {
            throw new AdminUsageException(_planHashFlag, $"must be the {_planHashLength}-character hex hash that the dry run printed.");
        }

        return Build(AdminAction.Apply, config, planHash);
    }

    private static ParsedArguments Build(AdminAction action, string? config, string? planHash)
    {
        if (config is null)
        {
            throw new AdminUsageException(_configFlag, "is required. Give the path to the setup file.");
        }

        return new ParsedArguments(action, config, planHash, HelpForSetup: false);
    }

    private static bool IsHelpFlag(string token) => token is "--help" or "-h";

    private static bool IsPlanHash(string text) =>
        text.Length == _planHashLength && text.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));

    private static void RejectFlag(bool present, string flag, string rule)
    {
        if (present)
        {
            throw new AdminUsageException(flag, rule);
        }
    }

    private static void RejectInline(string name, string? inline)
    {
        if (inline is not null)
        {
            throw new AdminUsageException(name, "is a switch and takes no value.");
        }
    }

    /// <summary>The value for a flag, from <c>=value</c> or the next token. A missing value names the flag only.</summary>
    private static string ValueOf(IReadOnlyList<string> args, ref int index, string name, string? inline)
    {
        if (inline is not null)
        {
            if (inline.Length == 0)
            {
                throw new AdminUsageException(name, "needs a value after the equals sign.");
            }

            return inline;
        }

        if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new AdminUsageException(name, "needs a value.");
        }

        index++;
        return args[index];
    }

    private static AdminUsageException UnknownToken(string name)
    {
        // Only text shaped like an option is named. Anything else could be a value typed in the wrong place.
        return IsOptionShape(name)
            ? new AdminUsageException(name, "is not an option for this command. Run the setup help to see the options.")
            : new AdminUsageException("argument", "is not an option. Options start with two dashes, such as --config.");
    }

    private static bool IsOptionShape(string name)
    {
        if (!name.StartsWith("--", StringComparison.Ordinal) || name.Length < 3)
        {
            return false;
        }

        return name[2..].All(c => c is (>= 'a' and <= 'z') or '-');
    }
}
