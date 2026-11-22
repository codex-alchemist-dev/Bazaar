namespace Bazaar.Admin;

/// <summary>What the command was asked to do.</summary>
public enum AdminAction
{
    Help,
    Validate,
    DryRun,
    Apply,
    Verify,
    Wizard,
    SecretSet,
}

/// <summary>
/// The parsed command line. <see cref="ConfigPath"/> is set for every action except help, unless the values come from <c>--set</c>
/// flags or environment variables instead of a file. <see cref="SetArguments"/> holds the <c>--set</c> values in the order given.
/// <see cref="HelpForSetup"/>, <see cref="HelpForKodo"/> and <see cref="HelpForSecret"/> choose which help text a help request prints.
/// <see cref="SecretName"/> is the reference of a <c>secret set</c> request. The value is never part of the parsed line.
/// </summary>
public sealed record ParsedArguments(
    AdminAction Action,
    string? ConfigPath,
    string? PlanHash,
    bool HelpForSetup,
    IReadOnlyList<string> SetArguments,
    bool HelpForKodo = false,
    bool HelpForSecret = false,
    string? SecretName = null);

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
    private const string _setFlag = "--set";
    private const string _validateWord = "validate";
    private const string _applyWord = "apply";
    private const int _planHashLength = 64;

    /// <summary>
    /// Parses the arguments, or throws <see cref="AdminUsageException"/>. <paramref name="environmentOverrides"/> is true when a
    /// <c>BAZAAR__SETUP__</c> variable gives values, which makes <c>--config</c> optional the same way a <c>--set</c> flag does.
    /// </summary>
    public static ParsedArguments Parse(IReadOnlyList<string> args, bool environmentOverrides = false)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count == 0)
        {
            throw new AdminUsageException("command", $"no command given. Run {AdminConstants.CommandName} --help.");
        }

        if (args.Any(IsHelpFlag) || args[0] == "help")
        {
            return new ParsedArguments(
                AdminAction.Help,
                null,
                null,
                HelpForSetup: args[0] == AdminConstants.SetupCommandWord,
                [],
                HelpForKodo: args[0] == AdminConstants.KodoCommandWord,
                HelpForSecret: args[0] == AdminConstants.SecretCommandWord);
        }

        if (args[0] == AdminConstants.KodoCommandWord)
        {
            return ParseKodo(args);
        }

        if (args[0] == AdminConstants.SecretCommandWord)
        {
            return ParseSecret(args);
        }

        if (args[0] != AdminConstants.SetupCommandWord)
        {
            throw new AdminUsageException("command", $"unknown command. The commands are {AdminConstants.SetupCommandWord} and {AdminConstants.KodoCommandWord}. Run {AdminConstants.CommandName} --help.");
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
        var sets = new List<string>();
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
                case _setFlag:
                    sets.Add(SetValueOf(args, ref index, inline));
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
            // A bare "setup" starts the interactive wizard (doc 45 section 1.1). Any flag makes it a scripted form instead.
            if (args.Count == 1)
            {
                return new ParsedArguments(AdminAction.Wizard, null, null, HelpForSetup: false, []);
            }

            if (!dryRun)
            {
                throw new AdminUsageException(AdminConstants.SetupCommandWord, $"needs {_validateWord}, {_applyWord}, or {_dryRunFlag}. Run {AdminConstants.CommandName} setup --help.");
            }

            RejectFlag(planHash is not null, _planHashFlag, "is only for apply.");
            return Build(AdminAction.DryRun, config, null, sets, environmentOverrides);
        }

        if (subcommand == _validateWord)
        {
            RejectFlag(dryRun, _dryRunFlag, "is not used with validate. Use setup --dry-run.");
            RejectFlag(planHash is not null, _planHashFlag, "is only for apply.");
            return Build(AdminAction.Validate, config, null, sets, environmentOverrides);
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

        return Build(AdminAction.Apply, config, planHash, sets, environmentOverrides);
    }

    /// <summary>
    /// The secret commands (doc 45 section 1.6). Only <c>secret set &lt;reference&gt;</c> exists. The value is read from standard
    /// input or a hidden prompt, so an extra argument is refused without being echoed: it may be a value typed in the wrong place.
    /// </summary>
    private static ParsedArguments ParseSecret(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || args[1].StartsWith('-'))
        {
            throw new AdminUsageException(AdminConstants.SecretCommandWord, $"needs {AdminConstants.SecretSetWord}. Run {AdminConstants.CommandName} secret --help.");
        }

        if (args[1] != AdminConstants.SecretSetWord)
        {
            throw new AdminUsageException(AdminConstants.SecretCommandWord, $"unknown secret command. The only one is {AdminConstants.SecretSetWord}.");
        }

        if (args.Count < 3 || args[2].StartsWith('-'))
        {
            throw new AdminUsageException("name", "is required. Give the secret reference first, for example secret://bazaar/providers/anthropic/key.");
        }

        if (args.Count > 3)
        {
            throw new AdminUsageException("argument", "takes one reference only. The value is never taken from the command line; it is read from standard input or a hidden prompt.");
        }

        return new ParsedArguments(AdminAction.SecretSet, null, null, HelpForSetup: false, [], SecretName: args[2]);
    }

    /// <summary>
    /// The Kodo commands (doc 46; doc 45 section 1.12). Only <c>kodo verify</c> exists. It takes <c>--config</c> and <c>--json</c>.
    /// The setup flags are refused here, so the verify path can never be asked to apply a plan.
    /// </summary>
    private static ParsedArguments ParseKodo(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || args[1].StartsWith('-'))
        {
            throw new AdminUsageException(AdminConstants.KodoCommandWord, $"needs {AdminConstants.KodoVerifyWord}. Run {AdminConstants.CommandName} kodo --help.");
        }

        if (args[1] != AdminConstants.KodoVerifyWord)
        {
            throw new AdminUsageException(AdminConstants.KodoCommandWord, $"unknown kodo command. The only one is {AdminConstants.KodoVerifyWord}.");
        }

        string? config = null;
        for (var index = 2; index < args.Count; index++)
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
                case _jsonFlag:
                    RejectInline(name, inline);
                    break;
                case _planHashFlag:
                    throw new AdminUsageException(_planHashFlag, "is only for setup apply.");
                case _dryRunFlag:
                    throw new AdminUsageException(_dryRunFlag, $"is not used with {AdminConstants.KodoCommandWord} {AdminConstants.KodoVerifyWord}.");
                default:
                    throw UnknownToken(name);
            }
        }

        return Build(AdminAction.Verify, config, null, [], false);
    }

    private static ParsedArguments Build(AdminAction action, string? config, string? planHash, List<string> sets, bool environmentOverrides)
    {
        // A file is optional when some value comes from a flag or an environment variable (doc 45 section 1.5, the overlay).
        if (config is null && sets.Count == 0 && !environmentOverrides)
        {
            throw new AdminUsageException(_configFlag, "is required unless --set or a BAZAAR__SETUP__ variable gives the values. Give the path to the setup file.");
        }

        return new ParsedArguments(action, config, planHash, HelpForSetup: false, sets);
    }

    /// <summary>
    /// The value of one <c>--set</c>, from <c>=value</c> or the next token. It must hold a path and an equals sign. The text is never
    /// echoed in an error, so a value typed in the wrong place cannot reach the output.
    /// </summary>
    private static string SetValueOf(IReadOnlyList<string> args, ref int index, string? inline)
    {
        var value = ValueOf(args, ref index, _setFlag, inline);
        var equals = value.IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0)
        {
            throw new AdminUsageException(_setFlag, "must be a path and a value joined by an equals sign, for example install.companyName=Example Studio.");
        }

        return value;
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
