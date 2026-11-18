using System.Text.Json;
using Bazaar.Admin.Composition;
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
    /// decides whether apply may run; left out, the gate is built from the database connection reference in the environment
    /// (see <see cref="PostgresApplyGate"/>) and is closed when no durable audit store is reachable.
    /// <paramref name="environment"/> supplies the <c>BAZAAR__SETUP__</c> variables; left out, the process environment is read.
    /// The wizard reads answers from <paramref name="prompter"/> (left out, the console); <c>secret set</c> reads its value from
    /// <paramref name="secretInput"/> and stores it in <paramref name="secretWriter"/>. Left out, both fail closed or read the console.
    /// </summary>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter stdout,
        TextWriter stderr,
        ICliApplyGate? applyGate = null,
        ISetupEnvironment? environment = null,
        ISetupPrompter? prompter = null,
        ISecretValueSource? secretInput = null,
        ISetupSecretWriter? secretWriter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        var variables = (environment ?? ProcessSetupEnvironment.Instance).Variables;

        // The JSON choice is read from the raw arguments first, so a usage error still answers a script in JSON.
        var json = args.Contains(_jsonFlag, StringComparer.Ordinal);
        ParsedArguments parsed;
        try
        {
            parsed = AdminArgumentParser.Parse(args, SetupDoorOverlay.HasEnvironmentOverrides(variables));
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

        // Only the commands that apply need a gate, so only they probe the database. A gate built here is disposed here.
        PostgresApplyGate? ownedGate = null;
        if (applyGate is null)
        {
            if (parsed.Action is AdminAction.Wizard or AdminAction.Apply)
            {
                ownedGate = await PostgresApplyGate.FromEnvironmentAsync(variables, cancellationToken);
                applyGate = ownedGate;
            }
            else
            {
                applyGate = PostgresApplyGate.Closed;
            }
        }

        try
        {
            return await DispatchAsync(parsed, applyGate, args, json, variables, stdout, stderr, prompter, secretInput, secretWriter, cancellationToken);
        }
        finally
        {
            if (ownedGate is not null)
            {
                await ownedGate.DisposeAsync();
            }
        }
    }

    private static async Task<int> DispatchAsync(
        ParsedArguments parsed,
        ICliApplyGate applyGate,
        IReadOnlyList<string> args,
        bool json,
        IReadOnlyDictionary<string, string?> variables,
        TextWriter stdout,
        TextWriter stderr,
        ISetupPrompter? prompter,
        ISecretValueSource? secretInput,
        ISetupSecretWriter? secretWriter,
        CancellationToken cancellationToken)
    {
        if (parsed.Action == AdminAction.Wizard)
        {
            var wizard = new SetupWizard(prompter ?? new ConsoleSetupPrompter(Console.In, stdout, secretInput ?? ConsoleSecretValueSource.Instance), variables, applyGate, secretWriter ?? new UnavailableSetupSecretWriter());
            var outcome = await wizard.RunAsync(cancellationToken);
            return outcome.ExitCode;
        }

        if (parsed.Action == AdminAction.SecretSet)
        {
            return await SecretCommands.SetAsync(
                parsed.SecretName!,
                secretWriter ?? new UnavailableSetupSecretWriter(),
                secretInput ?? ConsoleSecretValueSource.Instance,
                stdout,
                stderr,
                cancellationToken);
        }

        if (parsed.Action == AdminAction.Verify)
        {
            var verify = await KodoCommands.VerifyAsync(parsed.ConfigPath!, cancellationToken);
            EmitVerify(verify, json, stdout);
            return verify.ExitCode;
        }

        var report = parsed.Action switch
        {
            AdminAction.Validate => SetupOrchestrator.Validate(parsed.ConfigPath, parsed.SetArguments, variables),
            AdminAction.DryRun => SetupOrchestrator.DryRun(parsed.ConfigPath, parsed.SetArguments, variables),
            AdminAction.Apply => await SetupOrchestrator.ApplyAsync(
                parsed.ConfigPath, parsed.SetArguments, variables, parsed.PlanHash!, applyGate, cancellationToken),
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

        if (parsed.HelpForSecret)
        {
            return AdminHelp.Secret;
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

    private static SetupReport UsageReport(AdminUsageException exception) => new()
    {
        Command = "usage",
        ExitCode = (int)SetupExitCode.InvalidInput,
        Result = "invalid-input",
        Issues = [new SetupReportIssue { Severity = "error", Path = exception.Flag, Message = exception.Message }],
    };

    private static void Emit(SetupReport report, bool json, TextWriter stdout, TextWriter stderr)
    {
        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(report, AdminJsonContext.Default.SetupReport));
            return;
        }

        // A usage error is a failure, so its plain text goes to stderr. Results go to stdout.
        AdminTextRenderer.Write(report, report.Command == "usage" ? stderr : stdout);
    }
}
