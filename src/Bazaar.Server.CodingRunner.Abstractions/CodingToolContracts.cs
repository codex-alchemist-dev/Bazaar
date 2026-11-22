using System.Text.Json;
using Bazaar.Server.CodingPermissions;

namespace Bazaar.Server.CodingRunner;

/// <summary>One tool call from the model, as the Agents turn loop hands it to the runner.</summary>
/// <param name="AgentId">The calling agent.</param>
/// <param name="SessionId">The coding session.</param>
/// <param name="Tool">The tool name the model asked for. Untrusted text.</param>
/// <param name="Args">The parsed JSON arguments. The default (undefined) value is treated as an empty object.</param>
/// <param name="SourceTrust">The doc 15 source trust label of the content that caused the call.</param>
/// <param name="Taint">The session taint as the caller sees it. Defaults to hard, so an omitted value fails closed.</param>
/// <param name="Role">The session role. Defaults to the narrower reader role, so an omitted value fails closed.</param>
/// <param name="WorktreeKey">The worktree the call works in. Required for a handler that writes.</param>
/// <param name="TargetPath">The path the call touches, when it has one.</param>
/// <param name="TargetBranch">The branch a push or merge targets, when it has one.</param>
/// <param name="Grant">A verified per-action approval for this exact call, when one exists.</param>
public sealed record CodingToolCall(
    string AgentId,
    string SessionId,
    string Tool,
    JsonElement Args = default,
    string SourceTrust = "agent",
    SessionTaint Taint = SessionTaint.Hard,
    ToolSessionRole Role = ToolSessionRole.Reader,
    string? WorktreeKey = null,
    string? TargetPath = null,
    string? TargetBranch = null,
    ToolApprovalGrant? Grant = null);

/// <summary>What a handler returns: how the command ended and the JSON the model sees.</summary>
/// <param name="Outcome">The exit code and flags the recorder maps to a status.</param>
/// <param name="Output">The result as JSON text. Must carry no secrets or host paths.</param>
public sealed record CodingToolOutput(ExecOutcome Outcome, string Output);

/// <summary>One guarded tool body. Handlers are registered once; the runner never lets the model name anything else.</summary>
public interface ICodingToolHandler
{
    /// <summary>The exact tool name.</summary>
    string Name { get; }

    /// <summary>The capability class the gate binds for this tool (doc 17, "Tool set").</summary>
    ToolCapabilityClass CapabilityClass { get; }

    /// <summary>True when the tool changes the worktree. The runner serialises such calls per worktree.</summary>
    bool WritesWorktree { get; }

    /// <summary>Runs the tool. Model input problems come back as a non-zero <see cref="ExecOutcome"/>, not as exceptions.</summary>
    Task<CodingToolOutput> ExecuteAsync(CodingToolCall toolCall, CancellationToken cancellationToken);
}

/// <summary>How a call ended, as the model is told.</summary>
public enum CodingToolStatus
{
    /// <summary>The tool ran and exited 0.</summary>
    Ok,

    /// <summary>The tool ran and failed.</summary>
    Failed,

    /// <summary>The tool was killed when its timeout expired.</summary>
    TimedOut,

    /// <summary>The outcome is not known, such as after a crash.</summary>
    Unknown,

    /// <summary>The tool name is not registered.</summary>
    UnknownTool,

    /// <summary>The gate refused the call. No approval changes that.</summary>
    Denied,

    /// <summary>The gate wants an approval first.</summary>
    ApprovalRequired,

    /// <summary>The worktree is held by another writer and the wait ran out.</summary>
    Busy,

    /// <summary>The call could not be judged or recorded, so it did not run.</summary>
    Error,
}

/// <summary>The structured result the model receives. The runner never throws for a model-caused problem.</summary>
/// <param name="Status">How the call ended.</param>
/// <param name="ExitCode">The exit code, when the tool ran and had one.</param>
/// <param name="Output">The handler's JSON when it ran; otherwise the structured error JSON.</param>
/// <param name="ErrorCode">A short stable code for an error result; null for a tool result.</param>
/// <param name="Message">A short explanation for an error result. Never file content.</param>
/// <param name="CallId">The trace row id, when one was written.</param>
/// <param name="TraceFailed">True when the outcome could not be written to the row. The status is still the true one.</param>
public sealed record CodingToolResult(
    CodingToolStatus Status,
    int? ExitCode,
    string Output,
    string? ErrorCode = null,
    string? Message = null,
    Guid? CallId = null,
    bool TraceFailed = false)
{
    /// <summary>True only when the tool ran and succeeded.</summary>
    public bool IsOk => Status == CodingToolStatus.Ok;
}

/// <summary>Runs one model tool call through the gate, the worktree lock and the recorder.</summary>
public interface ICodingToolRunner
{
    /// <summary>
    /// Runs the call. Returns a structured result for every outcome except cancellation, which is recorded and then
    /// rethrown as <see cref="OperationCanceledException"/>.
    /// </summary>
    ValueTask<CodingToolResult> RunAsync(CodingToolCall toolCall, CancellationToken cancellationToken);
}
