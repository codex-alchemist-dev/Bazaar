using System.Text.Json;
using Bazaar.Admin.Placeholders;
using Bazaar.Server.Setup;

namespace Bazaar.Admin;

/// <summary>
/// The admin command: a hand-written parser and the dispatch to the setup commands. Output goes to the two writers it is given,
/// so tests capture it. Plain text goes to <paramref name="stdout"/> (usage errors to <paramref name="stderr"/>); <c>--json</c>
/// always writes its one document to <paramref name="stdout"/>.
/// </summary>
public static class AdminCommandLine
{
    private const string _jsonFlag = "--json";

    /// <summary>
    /// Runs the command and returns its exit code (0, 1, 2 or 3, the <see cref="SetupExitCode"/> values). <paramref name="applyGate"/>
    /// decides whether apply may run; left out, the placeholder gate is used, which is closed in this build.
    /// </summary>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter stdout,
        TextWriter stderr,
        ICliApplyGate? applyGate = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        applyGate ??= new PlaceholderApplyGate();

        // The JSON choice is read from the raw arguments first, so a usage error still answers a script in JSON.
        var json = args.Contains(_jsonFlag, StringComparer.Ordinal);
        ParsedArguments parsed;
        try
        {
            parsed = AdminArgumentParser.Parse(args);
        }
        catch (AdminUsageException exception)
        {
            var refused = UsageReport(exception);
            Emit(refused, json, stdout, stderr);
            return refused.ExitCode;
        }

        if (parsed.Action == AdminAction.Help)
        {
            stdout.Write(HelpText(parsed));
            return (int)SetupExitCode.Ok;
        }

        if (parsed.Action == AdminAction.Verify)
        {
            var verify = await KodoCommands.VerifyAsync(parsed.ConfigPath!, cancellationToken);
            EmitVerify(verify, json, stdout);
            return verify.ExitCode;
        }

        var report = parsed.Action switch
        {
            AdminAction.Validate => SetupCommands.Validate(parsed.ConfigPath!),
            AdminAction.DryRun => SetupCommands.DryRun(parsed.ConfigPath!),
            AdminAction.Apply => await SetupCommands.ApplyAsync(parsed.ConfigPath!, parsed.PlanHash!, applyGate, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(args), parsed.Action, "Unknown action."),
        };

        Emit(report, json, stdout, stderr);
        return report.ExitCode;
    }

    /// <summary>The help text for a help request. The Kodo help is chosen only when the first word is <c>kodo</c>.</summary>
    private static string HelpText(ParsedArguments parsed)
    {
        if (parsed.HelpForSetup)
        {
            return AdminHelp.Setup;
        }

        return parsed.HelpForKodo ? AdminHelp.Kodo : AdminHelp.Top;
    }

    private static void EmitVerify(KodoVerifyReport report, bool json, TextWriter stdout)
    {
        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(report, AdminJsonContext.Default.KodoVerifyReport));
            return;
        }

        AdminTextRenderer.WriteVerify(report, stdout);
    }

    private static AdminReport UsageReport(AdminUsageException exception) => new()
    {
        Command = "usage",
        ExitCode = (int)SetupExitCode.InvalidInput,
        Result = "invalid-input",
        Issues = [new AdminIssue { Severity = "error", Path = exception.Flag, Message = exception.Message }],
    };

    private static void Emit(AdminReport report, bool json, TextWriter stdout, TextWriter stderr)
    {
        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(report, AdminJsonContext.Default.AdminReport));
            return;
        }

        // A usage error is a failure, so its plain text goes to stderr. Results go to stdout.
        AdminTextRenderer.Write(report, report.Command == "usage" ? stderr : stdout);
    }
}
