using Bazaar.Admin.Placeholders;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Settings;
using Bazaar.Server.Setup;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Admin;

/// <summary>
/// The three setup commands (doc 45 section 1.7). Each one reads the file, then calls the setup library: the loader for
/// validation, the change planner for the dry run and the applier for apply. No rule is repeated here.
/// </summary>
internal static class SetupCommands
{
    /// <summary>The current-state label. This build has no state reader, so every dry run and apply compares against nothing.</summary>
    public const string EmptyState = "empty";

    /// <summary>Validates the file. The path is read and never echoed.</summary>
    public static AdminReport Validate(string configPath)
    {
        var read = ReadConfig(configPath);
        if (read.Issue is not null)
        {
            return Refused("validate", read.Issue);
        }

        return FromLoad("validate", SetupPlanLoader.Load(read.Text!), currentState: null);
    }

    /// <summary>
    /// Validates, then lists each change against an empty snapshot. Writes nothing. Held plans still list their changes, with
    /// exit 2, so the operator sees what a hold is blocking.
    /// </summary>
    public static AdminReport DryRun(string configPath)
    {
        var read = ReadConfig(configPath);
        if (read.Issue is not null)
        {
            return Refused("dry-run", read.Issue);
        }

        var load = SetupPlanLoader.Load(read.Text!);
        var report = FromLoad("dry-run", load, EmptyState);
        if (load.Plan is null)
        {
            return report;
        }

        var changes = SetupChangePlanner.Compute(load.Plan, SetupStateSnapshot.Empty).Changes;
        report.Changes.AddRange(changes.Select(SetupChangeView.ToChange));
        return report;
    }

    /// <summary>
    /// Applies the plan only when <paramref name="planHash"/> matches it. The steps list is empty in this build, so a plan with
    /// changes fails with exit 3 and nothing is written (the applier's own rule). Runs under placeholder lock and audit classes,
    /// which the Host adapter replaces (see <see cref="NullSetupInstanceLock"/>).
    /// </summary>
    public static async Task<AdminReport> ApplyAsync(string configPath, string planHash, CancellationToken cancellationToken)
    {
        var read = ReadConfig(configPath);
        if (read.Issue is not null)
        {
            return Refused("apply", read.Issue);
        }

        var load = SetupPlanLoader.Load(read.Text!);
        if (load.Plan is null)
        {
            return FromLoad("apply", load, EmptyState);
        }

        var services = new ServiceCollection();
        services.AddSingleton<ISetupInstanceLock>(new NullSetupInstanceLock());
        services.AddSingleton<IAuditLog>(new NullSetupAuditLog());
        services.AddSingleton<ISettingAuditSink>(new InMemorySettingAuditSink());
        services.AddSetupApply([]);

        using var provider = services.BuildServiceProvider();
        var applier = provider.GetRequiredService<SetupApplier>();
        var request = new SetupApplyRequest(load.Plan, planHash, SetupEntryPath.Cli, OperatorIdentity(), null, null, null);
        var result = await applier.ApplyAsync(request, cancellationToken);

        return new AdminReport
        {
            Command = "apply",
            ExitCode = (int)result.ExitCode,
            Result = ResultFor("apply", result.ExitCode),
            PlanHash = result.PlanHash,
            CurrentState = EmptyState,
            Issues = result.Issues.Select(SetupChangeView.ToIssue).ToList(),
            AppliedSteps = result.AppliedStepIds.ToList(),
        };
    }

    /// <summary>The operator recorded in audit rows. The CLI has no sign-in, so it is the local user name.</summary>
    private static string OperatorIdentity()
    {
        var name = Environment.UserName;
        return string.IsNullOrWhiteSpace(name) ? "local-operator" : name;
    }

    private static AdminReport FromLoad(string command, SetupLoadResult load, string? currentState)
    {
        var code = load.ExitCode;
        return new AdminReport
        {
            Command = command,
            ExitCode = (int)code,
            Result = ResultFor(command, code),
            PlanHash = load.Plan is null ? null : load.PlanHash,
            CurrentState = currentState,
            Issues = load.Issues.Select(SetupChangeView.ToIssue).ToList(),
        };
    }

    /// <summary>A report for input that could not be read. No plan is built, so there is no hash.</summary>
    private static AdminReport Refused(string command, AdminIssue issue) => new()
    {
        Command = command,
        ExitCode = (int)SetupExitCode.InvalidInput,
        Result = ResultFor(command, SetupExitCode.InvalidInput),
        Issues = [issue],
    };

    /// <summary>The result slug for an exit code. Apply succeeds as "applied"; a check succeeds as "valid".</summary>
    internal static string ResultFor(string command, SetupExitCode code) => code switch
    {
        SetupExitCode.Ok => command == "apply" ? "applied" : "valid",
        SetupExitCode.InvalidInput => "invalid-input",
        SetupExitCode.Held => "held",
        SetupExitCode.ApplyFailedRolledBack => "apply-failed-rolled-back",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown exit code."),
    };

    /// <summary>Reads the file. The path is named by its flag only, so an unreadable path never appears in output.</summary>
    private static (string? Text, AdminIssue? Issue) ReadConfig(string configPath)
    {
        try
        {
            return (File.ReadAllText(configPath), null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (null, new AdminIssue
            {
                Severity = "error",
                Path = "--config",
                Message = "the file cannot be read. Check that it exists and that this user can read it.",
            });
        }
    }
}
