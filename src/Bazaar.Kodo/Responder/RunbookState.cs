using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Kodo.Evaluator;

namespace Bazaar.Kodo.Responder;

/// <summary>Where a run stands. The last four are final.</summary>
public enum RunbookPhase
{
    Running,
    AwaitingApproval,
    Succeeded,
    RolledBack,
    Denied,
    Failed,
    NeedsHuman,
}

/// <summary>Which step list a run is in.</summary>
public enum RunbookSection
{
    Preconditions,
    Steps,
    Rollback,
}

/// <summary>An approval a run holds, by step.</summary>
/// <param name="StepId">The step id, prefixed <c>rollback:</c> for a rollback step.</param>
/// <param name="ApprovalId">The gate's approval id.</param>
public sealed record RunbookApprovalRecord(string StepId, string ApprovalId);

/// <summary>
/// Everything a run needs to continue after a restart (section 16.2: replayable, and a restart must not repeat or skip a step).
/// Persisted after each transition through <see cref="IRunbookStateStore"/>. Identifiers and counters only.
/// </summary>
public sealed record RunbookRunState(
    int Version,
    string RunId,
    string RunbookId,
    int RunbookVersion,
    string Digest,
    RunbookPhase Phase,
    string? Cause,
    RunbookSection Section,
    int StepIndex,
    int Attempts,
    bool InFlight,
    DateTimeOffset? NotBefore,
    DateTimeOffset? ApprovalRequestedAt,
    bool Gated,
    bool Tainted,
    bool ActionsAttempted,
    RunbookPhase AfterRollback,
    string Starter,
    IReadOnlyList<RunbookApprovalRecord> Approvals,
    IReadOnlyDictionary<string, string> Inputs,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    long Sequence)
{
    /// <summary>The state format version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>True when the run has ended.</summary>
    public bool IsFinal => Phase is RunbookPhase.Succeeded or RunbookPhase.RolledBack or RunbookPhase.Denied or RunbookPhase.Failed or RunbookPhase.NeedsHuman;
}

/// <summary>Source-generated JSON for run state. camelCase, enums as words, unknown members rejected.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(RunbookRunState))]
internal sealed partial class ResponderJsonContext : JsonSerializerContext
{
}

/// <summary>Reads and writes <see cref="RunbookRunState"/>. Parsing never throws on bad input.</summary>
public static class RunbookRunStateCodec
{
    /// <summary>Larger input is refused before parsing.</summary>
    public const int MaxBytes = 256 * 1024;

    /// <summary>Encodes a state.</summary>
    public static byte[] ToUtf8Bytes(RunbookRunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return JsonSerializer.SerializeToUtf8Bytes(state, ResponderJsonContext.Default.RunbookRunState);
    }

    /// <summary>Parses a saved state. False for oversized, malformed, wrong-version, unknown-member or out-of-range input.</summary>
    public static bool TryParse(ReadOnlySpan<byte> utf8, out RunbookRunState? state)
    {
        state = null;
        if (utf8.Length == 0 || utf8.Length > MaxBytes)
        {
            return false;
        }

        try
        {
            RunbookRunState? parsed = JsonSerializer.Deserialize(utf8, ResponderJsonContext.Default.RunbookRunState);
            if (parsed is null || !IsSane(parsed))
            {
                return false;
            }

            state = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsSane(RunbookRunState s) =>
        s.Version == RunbookRunState.CurrentVersion
        && !string.IsNullOrEmpty(s.RunId) && !string.IsNullOrEmpty(s.RunbookId) && !string.IsNullOrEmpty(s.Digest)
        && s.RunbookVersion >= 1 && s.StepIndex is >= 0 and <= RunbookDefinition.MaxStepsPerList
        && s.Attempts is >= 0 and <= 5 && s.Sequence >= 0
        && Enum.IsDefined(s.Phase) && Enum.IsDefined(s.Section) && Enum.IsDefined(s.AfterRollback)
        && s.Approvals is not null && s.Approvals.Count <= RunbookDefinition.MaxStepsPerList * 2
        && s.Inputs is not null && s.Inputs.Count <= 16 && s.Starter is not null;
}

/// <summary>A run's state for the <c>kodo.runbook.finished</c> webhook (section 15.3): identifiers only.</summary>
public static class RunbookWebhook
{
    /// <summary>The finished-event identifiers: run id, runbook id and the final phase word.</summary>
    public static Export.KodoWebhookEnvelope Finished(RunbookRunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Export.KodoWebhookEnvelopeBuilder.Build(
            Export.KodoWebhookEvents.RunbookFinished,
            $"{state.RunId}-{state.Sequence}",
            state.UpdatedAt,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["run_id"] = state.RunId,
                ["runbook_id"] = state.RunbookId,
                ["phase"] = state.Phase.ToString().ToLowerInvariant(),
            });
    }
}

/// <summary>What a runner reports for one step.</summary>
public enum RunbookStepOutcome
{
    /// <summary>The step did what it should.</summary>
    Succeeded,

    /// <summary>The step failed and will not pass on retry.</summary>
    Failed,

    /// <summary>The step failed without any effect and may pass on retry.</summary>
    Transient,

    /// <summary>The runner did not finish in time. The result is unknown.</summary>
    TimedOut,
}

/// <summary>What the runner is asked to do. The idempotency key is the same on every attempt of one step.</summary>
public sealed record RunbookStepRequest(
    string RunId,
    string RunbookId,
    string StepId,
    RunbookStepKind Kind,
    string Operation,
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyDictionary<string, string> Inputs,
    string IdempotencyKey,
    int Attempt,
    TimeSpan Timeout);

/// <summary>The runner's answer. <paramref name="Detail"/> is a short code for the journal, not free text.</summary>
public sealed record RunbookStepResult(RunbookStepOutcome Outcome, string Detail);

/// <summary>What a dry run would change, as identifier-safe lines.</summary>
public sealed record RunbookPreview(string StepId, string Summary);

/// <summary>
/// Carries out steps (section 16.2). The implementation does the I/O (sentinel, database, files). It reports a definite failure as
/// <see cref="RunbookStepOutcome.Failed"/> and may report <see cref="RunbookStepOutcome.Transient"/> only when it changed nothing.
/// </summary>
public interface IRunbookActionRunner
{
    /// <summary>Runs one step.</summary>
    ValueTask<RunbookStepResult> RunAsync(RunbookStepRequest request, CancellationToken cancellationToken);

    /// <summary>Shows what an action step would change, without changing it (the dry-run preview of one-click fixes).</summary>
    ValueTask<RunbookPreview> PreviewAsync(RunbookStepRequest request, CancellationToken cancellationToken);
}

/// <summary>One journal entry of a run (section 16.2: the journal holds the inputs, the step results and the approvals).</summary>
/// <param name="RunId">The run.</param>
/// <param name="Sequence">The state sequence the entry belongs to.</param>
/// <param name="At">When.</param>
/// <param name="Kind">One of <see cref="RunbookJournalKinds"/>.</param>
/// <param name="StepId">The step, or null for a run-level entry.</param>
/// <param name="Detail">A short code, or null.</param>
public sealed record RunbookJournalEntry(string RunId, long Sequence, DateTimeOffset At, string Kind, string? StepId, string? Detail);

/// <summary>The journal entry kinds.</summary>
public static class RunbookJournalKinds
{
    public const string RunStarted = "run_started";
    public const string RunRefused = "run_refused";
    public const string StepStarted = "step_started";
    public const string StepSucceeded = "step_succeeded";
    public const string StepFailed = "step_failed";
    public const string StepRetry = "step_retry";
    public const string ApprovalRequested = "approval_requested";
    public const string ApprovalGranted = "approval_granted";
    public const string ApprovalDenied = "approval_denied";
    public const string RollbackStarted = "rollback_started";
    public const string RunFinished = "run_finished";
}

/// <summary>
/// The journal seam for runs. Every step is journalled before it starts and after it ends. The implementation throws
/// <see cref="IOException"/> when it cannot write; the engine then stops the run as needs-human.
/// </summary>
public interface IRunbookJournal
{
    /// <summary>Appends one entry.</summary>
    ValueTask AppendAsync(RunbookJournalEntry entry, CancellationToken cancellationToken);
}

/// <summary>
/// Persists run state so a restart can continue. A save replaces the stored state only when its sequence is higher; the implementation
/// throws <see cref="IOException"/> when it cannot save, and the engine stops the run.
/// </summary>
public interface IRunbookStateStore
{
    /// <summary>Loads a run, or null when none exists.</summary>
    ValueTask<RunbookRunState?> LoadAsync(string runId, CancellationToken cancellationToken);

    /// <summary>Saves a run.</summary>
    ValueTask SaveAsync(RunbookRunState state, CancellationToken cancellationToken);
}

/// <summary>What an approval gate decided.</summary>
public enum ApprovalDecision
{
    /// <summary>Not decided yet. Ask again later.</summary>
    Pending,

    /// <summary>Explicitly approved. The only value that lets a step run.</summary>
    Approved,

    /// <summary>Explicitly refused.</summary>
    Denied,

    /// <summary>The gate gave up waiting. Never counts as approval.</summary>
    TimedOut,
}

/// <summary>An approval request for one step (doc 15). The digest binds an approval to exactly this step of this run.</summary>
public sealed record ApprovalRequest(
    string RunId,
    string RunbookId,
    int RunbookVersion,
    string StepId,
    string Operation,
    string RequestDigest,
    RunbookRisk Risk,
    bool RequireStepUp,
    bool Tainted,
    string RequiredControl);

/// <summary>An approval answer. It counts only when <paramref name="RequestDigest"/> matches the request, and, where step-up was asked, when it was satisfied.</summary>
public sealed record ApprovalResult(ApprovalDecision Decision, string ApprovalId, string RequestDigest, bool StepUpSatisfied);

/// <summary>
/// The approval wiring to doc 15's gates. <see cref="RequestAsync"/> is idempotent per request digest: asking again returns the same
/// approval. The engine never treats a timeout, an error or a mismatched answer as approval.
/// </summary>
public interface IApprovalGate
{
    /// <summary>Asks for, or looks up, the approval of one step.</summary>
    ValueTask<ApprovalResult> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>What a start request carries.</summary>
/// <param name="RunId">A caller-chosen id, URL-safe, unique per run. A repeated id is refused, so a retry cannot run twice.</param>
/// <param name="RunbookId">The runbook.</param>
/// <param name="RunbookVersion">The version.</param>
/// <param name="ExpectedDigest">The digest the caller saw, or null. A mismatch means the runbook changed and the start is refused.</param>
/// <param name="Starter">Who started it, as an identifier.</param>
/// <param name="GrantedControls">The controls the starter holds (the role model answers; the engine only checks the name).</param>
/// <param name="Inputs">Identifier inputs for the runner.</param>
/// <param name="InputsTainted">True when any input came from untrusted content (section 16.2). The run then needs approval.</param>
/// <param name="State">The current displayed Kodo state.</param>
public sealed record RunbookStartRequest(
    string RunId,
    string RunbookId,
    int RunbookVersion,
    string? ExpectedDigest,
    string Starter,
    IReadOnlySet<string> GrantedControls,
    IReadOnlyDictionary<string, string> Inputs,
    bool InputsTainted,
    KodoState State);

/// <summary>The result of a start.</summary>
/// <param name="Started">True when the run was created.</param>
/// <param name="Refusal">The refusal code, or null.</param>
/// <param name="State">The run's state after its first advance, or null when refused.</param>
public sealed record RunbookStartResult(bool Started, string? Refusal, RunbookRunState? State);
