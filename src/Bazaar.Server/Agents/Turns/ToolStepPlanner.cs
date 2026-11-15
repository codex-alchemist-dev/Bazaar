using Bazaar.Server.Agents.Data;
using Bazaar.Server.Agents.Turns.Runner;

namespace Bazaar.Server.Agents.Turns;

/// <summary>What a requested tool call does, given the step an abandoned turn left for the same call (doc 06).</summary>
public enum ToolStepAction
{
    /// <summary>No usable step: the call is recorded as a new step and dispatched.</summary>
    Dispatch,

    /// <summary>The call finished before the crash. Its stored response is reused, and the tool does not run again.</summary>
    Reuse,

    /// <summary>The call was in flight and cannot run again. The model is told that its outcome is unknown.</summary>
    Unknown,
}

/// <summary>
/// The rules for tool steps across a crash, and the text the model reads for each result (doc 06, "Concurrency and recovery").
/// Pure, so the rules are testable without a database.
/// </summary>
public static class ToolStepPlanner
{
    /// <summary>What the model is told about a call that was in flight at a crash and is not idempotent.</summary>
    public const string UnknownResult =
        "The tool call was in flight when the runtime restarted and is not idempotent, so it was not run again. " +
        "Whether it took effect is unknown; do not assume it succeeded or failed.";

    /// <summary>
    /// Decides a call from the step an abandoned turn left for it, or null when there is none. Recovery has already settled
    /// those steps, so a pending one is treated like an abandoned one: it runs again only if the step was recorded as idempotent
    /// and the tool is still marked idempotent now (fail closed); otherwise it is unknown.
    /// </summary>
    /// <param name="prior">The step an abandoned turn left for this call, or null.</param>
    /// <param name="idempotentNow">Whether the tool is marked idempotent in the current settings.</param>
    public static ToolStepAction Plan(TurnStepRecord? prior, bool idempotentNow) => prior switch
    {
        null => ToolStepAction.Dispatch,
        { Status: TurnStepStatus.Completed } => ToolStepAction.Reuse,
        { Status: TurnStepStatus.Unknown } => ToolStepAction.Unknown,
        _ => prior.Idempotent && idempotentNow ? ToolStepAction.Dispatch : ToolStepAction.Unknown,
    };

    /// <summary>
    /// The status an unfinished step is settled to (doc 06). A model call or an idempotent tool is abandoned and may be
    /// re-issued; any other tool call is unknown, and the model is told.
    /// </summary>
    public static TurnStepStatus UnfinishedStatus(TurnStepKind kind, bool idempotent) =>
        kind == TurnStepKind.Model || (kind == TurnStepKind.Tool && idempotent) ? TurnStepStatus.Abandoned : TurnStepStatus.Unknown;

    /// <summary>
    /// The tool message content the model reads for a call's outcome. A refused call says it did not run, so the model does not
    /// assume that it did.
    /// </summary>
    public static string ResultText(ToolDispatchOutcome outcome, string? resultJson, string? reason) => outcome switch
    {
        ToolDispatchOutcome.Executed => resultJson ?? "null",
        ToolDispatchOutcome.Denied => reason is null
            ? "The tool call was denied by policy and did not run."
            : $"The tool call was denied by policy and did not run: {reason}",
        ToolDispatchOutcome.PendingApproval => "The tool call needs owner approval and did not run.",
        ToolDispatchOutcome.Unknown => UnknownResult,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown tool dispatch outcome."),
    };

    /// <summary>
    /// The request hash of a tool step. It covers the model request the call came from and the call as the model sent it, so a
    /// recovered turn finds the same step, and two calls with the same id in different responses stay apart.
    /// </summary>
    public static string Hash(string modelRequestHash, ModelStepToolCall call) =>
        TurnStepHash.Of(modelRequestHash, call.CallId, call.Name, call.ArgumentsJson);
}
