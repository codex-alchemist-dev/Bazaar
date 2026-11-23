using System.Text.RegularExpressions;

namespace Bazaar.Kodo.SelfChecks;

/// <summary>How one self-check run ended.</summary>
public enum SelfCheckOutcome
{
    /// <summary>The check ran, looked at something, and found nothing wrong. The only outcome that is green.</summary>
    Passed,

    /// <summary>The check ran and found a problem.</summary>
    Failed,

    /// <summary>The check did not run to a verdict: no runner, a missing precondition, or nothing to check. Amber, never green.</summary>
    Skipped,

    /// <summary>The check did not finish in time. Amber, never green.</summary>
    TimedOut,

    /// <summary>The runner threw. Amber, never green: the check could not say anything.</summary>
    Faulted,
}

/// <summary>The traffic light of one result.</summary>
public enum SelfCheckStatus
{
    Green,
    Amber,
    Red,
}

/// <summary>Stable cause codes (doc 46 section 17.6). Runners may add their own, in the same shape.</summary>
public static class SelfCheckCauses
{
    public const string Ok = "ok";
    public const string Mismatch = "checksum_mismatch";
    public const string RestoreFailed = "restore_failed";
    public const string SchemaVersionMismatch = "schema_version_mismatch";
    public const string GroupChatIntegrityFailed = "group_chat_integrity_failed";
    public const string VaultIndexFailed = "vault_index_failed";
    public const string BundleUnusable = "bundle_unusable";
    public const string DrillFailed = "drill_failed";
    public const string NoRunner = "no_runner";
    public const string PreconditionMissing = "precondition_missing";
    public const string NothingChecked = "nothing_checked";
    public const string Timeout = "timeout";
    public const string RunnerException = "runner_exception";
    public const string InvalidCause = "invalid_cause";
    public const string NeverRun = "never_run";
    public const string Stale = "stale";
}

/// <summary>What a runner reports. The scheduler turns it into a <see cref="SelfCheckResult"/> and applies the never-green rule.</summary>
public enum SelfCheckRunnerOutcome
{
    Passed,
    Failed,
    Skipped,
}

/// <summary>A runner's answer. Identifiers and numbers only.</summary>
/// <param name="Outcome">What the runner found.</param>
/// <param name="Cause">A stable lower-case code. <c>ok</c> for a pass.</param>
/// <param name="ItemsChecked">How many things the run actually looked at (files hashed, tables checked, bundles opened). A pass with none is not a pass.</param>
public sealed record SelfCheckRunnerResult(SelfCheckRunnerOutcome Outcome, string Cause, int ItemsChecked);

/// <summary>
/// The recorded result of one run (doc 46 section 17.2). Built only through the factories, which apply the never-green rule: a result is
/// green only when the check ran, looked at something, and passed.
/// </summary>
public sealed partial record SelfCheckResult
{
    private SelfCheckResult()
    {
    }

    /// <summary>The definition this is a result of.</summary>
    public required string DefinitionId { get; init; }

    /// <summary>Which check it was.</summary>
    public required SelfCheckKind Kind { get; init; }

    /// <summary>The due instant this run answers.</summary>
    public required DateTimeOffset Slot { get; init; }

    /// <summary>When the run started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When the run ended.</summary>
    public required DateTimeOffset FinishedAt { get; init; }

    /// <summary>How it ended.</summary>
    public required SelfCheckOutcome Outcome { get; init; }

    /// <summary>A stable lower-case code.</summary>
    public required string Cause { get; init; }

    /// <summary>How many things the run looked at.</summary>
    public required int ItemsChecked { get; init; }

    /// <summary>Green only for a pass, red only for a failure, amber for everything else.</summary>
    public SelfCheckStatus Status => Outcome switch
    {
        SelfCheckOutcome.Passed => SelfCheckStatus.Green,
        SelfCheckOutcome.Failed => SelfCheckStatus.Red,
        _ => SelfCheckStatus.Amber,
    };

    /// <summary>True when <paramref name="cause"/> has the shape of a stable code.</summary>
    public static bool IsValidCause(string? cause) => !string.IsNullOrEmpty(cause) && CausePattern().IsMatch(cause);

    /// <summary>
    /// A result from a runner's answer. A pass that checked nothing becomes <see cref="SelfCheckOutcome.Skipped"/> with cause
    /// <see cref="SelfCheckCauses.NothingChecked"/>; a pass with a cause other than <c>ok</c> keeps the pass but reports <c>ok</c>; a cause that is not code-shaped becomes
    /// <see cref="SelfCheckCauses.InvalidCause"/>; a negative count becomes zero.
    /// </summary>
    public static SelfCheckResult FromRunner(SelfCheckDefinition definition, DateTimeOffset slot, DateTimeOffset startedAt, DateTimeOffset finishedAt, SelfCheckRunnerResult? answer)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (answer is null || !Enum.IsDefined(answer.Outcome))
        {
            return Build(definition, slot, startedAt, finishedAt, SelfCheckOutcome.Faulted, SelfCheckCauses.RunnerException, 0);
        }

        int items = Math.Max(0, answer.ItemsChecked);
        string cause = IsValidCause(answer.Cause) ? answer.Cause : SelfCheckCauses.InvalidCause;
        switch (answer.Outcome)
        {
            case SelfCheckRunnerOutcome.Passed when items == 0:
                return Build(definition, slot, startedAt, finishedAt, SelfCheckOutcome.Skipped, SelfCheckCauses.NothingChecked, 0);
            case SelfCheckRunnerOutcome.Passed:
                return Build(definition, slot, startedAt, finishedAt, SelfCheckOutcome.Passed, SelfCheckCauses.Ok, items);
            case SelfCheckRunnerOutcome.Failed:
                return Build(definition, slot, startedAt, finishedAt, SelfCheckOutcome.Failed, cause, items);
            default:
                return Build(definition, slot, startedAt, finishedAt, SelfCheckOutcome.Skipped, cause, items);
        }
    }

    /// <summary>A result for a run that did not start, with a cause such as <see cref="SelfCheckCauses.NoRunner"/>.</summary>
    public static SelfCheckResult Skipped(SelfCheckDefinition definition, DateTimeOffset slot, DateTimeOffset now, string cause)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Build(definition, slot, now, now, SelfCheckOutcome.Skipped, IsValidCause(cause) ? cause : SelfCheckCauses.InvalidCause, 0);
    }

    /// <summary>A result for a run that did not finish in time.</summary>
    public static SelfCheckResult TimedOut(SelfCheckDefinition definition, DateTimeOffset slot, DateTimeOffset startedAt, DateTimeOffset finishedAt) =>
        Build(definition, slot, startedAt, finishedAt, SelfCheckOutcome.TimedOut, SelfCheckCauses.Timeout, 0);

    /// <summary>A result for a runner that threw.</summary>
    public static SelfCheckResult Faulted(SelfCheckDefinition definition, DateTimeOffset slot, DateTimeOffset startedAt, DateTimeOffset finishedAt) =>
        Build(definition, slot, startedAt, finishedAt, SelfCheckOutcome.Faulted, SelfCheckCauses.RunnerException, 0);

    private static SelfCheckResult Build(SelfCheckDefinition definition, DateTimeOffset slot, DateTimeOffset startedAt, DateTimeOffset finishedAt, SelfCheckOutcome outcome, string cause, int items) =>
        new()
        {
            DefinitionId = definition.Id,
            Kind = definition.Kind,
            Slot = slot,
            StartedAt = startedAt,
            FinishedAt = finishedAt < startedAt ? startedAt : finishedAt,
            Outcome = outcome,
            Cause = cause,
            ItemsChecked = items,
        };

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex CausePattern();
}
