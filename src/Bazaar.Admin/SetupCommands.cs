using Bazaar.Server.Setup;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Admin;

/// <summary>
/// The three setup commands (doc 45 section 1.7). Each one merges the doors (the file, the BAZAAR__SETUP__ variables and the
/// <c>--set</c> flags) with the overlay, then calls the setup library: the loader for validation, the change planner for the dry
/// run and the applier for apply. No rule is repeated here, except the apply hash comparison and the gate, which run before the
/// applier so a closed gate never reaches it.
/// </summary>
internal static class SetupCommands
{
    /// <summary>The current-state label. This build has no state reader, so every dry run and apply compares against nothing.</summary>
    public const string EmptyState = "empty";

    /// <summary>The outcome for an apply that the gate stopped. Scripts read it from the JSON document.</summary>
    public const string ApplyUnavailableOutcome = "apply_unavailable";

    /// <summary>The one plain line an unavailable apply prints. It names the missing store and the adapter that provides it.</summary>
    public const string ApplyUnavailableMessage =
        "apply is unavailable: this build has no durable audit store (doc 15 audit_log); the Host adapter provides it";

    /// <summary>The message for a hash that does not match the plan. It is the applier's own wording, so the two paths agree.</summary>
    private const string _hashMismatchMessage = "does not match the plan; run the dry run again and apply the hash it prints.";

    /// <summary>Validates the merged plan. The file path is read and never echoed.</summary>
    public static AdminReport Validate(string? configPath, IReadOnlyList<string> setArguments, IReadOnlyDictionary<string, string?> environment)
    {
        var merged = Merge("validate", configPath, setArguments, environment);
        if (merged.Refusal is not null)
        {
            return merged.Refusal;
        }

        return FromLoad("validate", merged.Load!, currentState: null);
    }

    /// <summary>
    /// Validates, then lists each change against an empty snapshot. Writes nothing. Held plans still list their changes, with
    /// exit 2, so the operator sees what a hold is blocking.
    /// </summary>
    public static AdminReport DryRun(string? configPath, IReadOnlyList<string> setArguments, IReadOnlyDictionary<string, string?> environment)
    {
        var merged = Merge("dry-run", configPath, setArguments, environment);
        if (merged.Refusal is not null)
        {
            return merged.Refusal;
        }

        var load = merged.Load!;
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
    /// Applies the plan. The order is fixed: merge the doors and load (errors exit 1, holds exit 2), then the plan-hash comparison
    /// (a mismatch exits 1), then the gate. A closed gate exits 2 with <see cref="ApplyUnavailableMessage"/> and never calls the
    /// applier. Only an open gate builds the applier, which then runs its own checks. The entry path comes from the doors used.
    /// </summary>
    public static async Task<AdminReport> ApplyAsync(
        string? configPath,
        IReadOnlyList<string> setArguments,
        IReadOnlyDictionary<string, string?> environment,
        string planHash,
        ICliApplyGate gate,
        CancellationToken cancellationToken,
        SetupEntryPath? entryPath = null)
    {
        ArgumentNullException.ThrowIfNull(gate);

        var merged = Merge("apply", configPath, setArguments, environment);
        if (merged.Refusal is not null)
        {
            return merged.Refusal;
        }

        var load = merged.Load!;
        if (load.Plan is null || load.ExitCode != SetupExitCode.Ok)
        {
            // Errors (1) and holds (2) stop here, before the hash is compared or the gate is asked.
            return FromLoad("apply", load, EmptyState);
        }

        if (!string.Equals(load.PlanHash, planHash, StringComparison.OrdinalIgnoreCase))
        {
            return new AdminReport
            {
                Command = "apply",
                ExitCode = (int)SetupExitCode.InvalidInput,
                Result = ResultFor("apply", SetupExitCode.InvalidInput),
                PlanHash = load.PlanHash,
                CurrentState = EmptyState,
                Issues = [new AdminIssue { Severity = "error", Path = "planHash", Message = _hashMismatchMessage }],
            };
        }

        if (!gate.DurableAuditStoreAvailable)
        {
            return new AdminReport
            {
                Command = "apply",
                ExitCode = (int)SetupExitCode.Held,
                Result = ResultFor("apply", SetupExitCode.Held),
                Outcome = ApplyUnavailableOutcome,
                PlanHash = load.PlanHash,
                CurrentState = EmptyState,
                Issues = [new AdminIssue { Severity = "hold", Path = "audit", Message = ApplyUnavailableMessage }],
            };
        }

        var services = new ServiceCollection();
        gate.RegisterApplyServices(services);
        services.AddSetupApply([]);

        using var provider = services.BuildServiceProvider();
        var applier = provider.GetRequiredService<SetupApplier>();
        var request = new SetupApplyRequest(load.Plan, planHash, entryPath ?? merged.EntryPath, OperatorIdentity(), null, null, null);
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

    /// <summary>
    /// Reads the file (when one is given), merges it with the environment variables and the flags, then loads the result. Either
    /// <see cref="Refusal"/> is set, or <see cref="Load"/> and <see cref="EntryPath"/> are.
    /// </summary>
    private static (SetupLoadResult? Load, SetupEntryPath EntryPath, AdminReport? Refusal) Merge(
        string command,
        string? configPath,
        IReadOnlyList<string> setArguments,
        IReadOnlyDictionary<string, string?> environment)
    {
        string? text = null;
        if (configPath is not null)
        {
            var read = ReadConfig(configPath);
            if (read.Issue is not null)
            {
                return (null, default, Refused(command, read.Issue));
            }

            text = read.Text;
        }

        var overlay = SetupDoorOverlay.Merge(text, setArguments, environment);
        if (overlay.HasErrors || overlay.MergedJson is null)
        {
            // A door error stops here: the loader does not run on a partial merge, and no plan hash exists.
            return (null, default, FromIssues(command, overlay.Issues));
        }

        return (SetupPlanLoader.Load(overlay.MergedJson), overlay.EntryPath, null);
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

    /// <summary>A report for door errors. Invalid input (1) is the only code these can give, and no plan hash exists.</summary>
    private static AdminReport FromIssues(string command, IReadOnlyList<SetupValidationIssue> issues)
    {
        var code = SetupExitCodes.ForIssues(issues);
        return new AdminReport
        {
            Command = command,
            ExitCode = (int)code,
            Result = ResultFor(command, code),
            CurrentState = command == "validate" ? null : EmptyState,
            Issues = issues.Select(SetupChangeView.ToIssue).ToList(),
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
    internal static (string? Text, AdminIssue? Issue) ReadConfig(string configPath)
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
