using Bazaar.Server.CodingPermissions;
using Bazaar.Server.Subagents;

namespace Bazaar.Server.CodingRunner.Handlers;

/// <summary>
/// The <c>subagent_spawn</c> tool: starts a child session with fresh context and waits for its result (doc 17, subagents; doc 06
/// limits). Arguments: <c>task</c> (required), <c>tool_allowlist</c> (required, a closed set that must be a subset of the parent's),
/// <c>model_id</c> (required, must be in the parent's whitelist), <c>worktree</c> (<c>none</c>, <c>read_only</c> or <c>own</c>,
/// default <c>none</c>), <c>declared_paths</c> and <c>references</c>. The parent limits come from the host for this call's session,
/// never from the arguments. The child's summary is model output and is returned as data, flagged untrusted.
/// </summary>
public sealed class SubagentSpawnToolHandler(ISubagentSpawner spawner, ICodingToolHostContext host) : ICodingToolHandler
{
    /// <summary>The tool name.</summary>
    public const string ToolName = "subagent_spawn";

    private const int _maxTask = 20_000;
    private const int _maxTools = 64;
    private const int _maxToolName = 128;
    private const int _maxModel = 128;
    private const int _maxPaths = 256;
    private const int _maxPath = 1024;
    private const int _maxReferences = 64;

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public ToolCapabilityClass CapabilityClass => ToolCapabilityClass.Exec;

    /// <inheritdoc />
    public bool WritesWorktree => false;

    /// <inheritdoc />
    public async Task<CodingToolOutput> ExecuteAsync(CodingToolCall toolCall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        SubagentSpec spec;
        try
        {
            var args = new CodingToolArgs(toolCall.Args, "task", "tool_allowlist", "model_id", "worktree", "declared_paths", "references");
            var allowlist = args.OptionalStringArray("tool_allowlist", _maxTools, _maxToolName)
                ?? throw new CodingToolArgumentException("tool_allowlist", "is required");
            spec = new SubagentSpec(
                toolCall.SessionId,
                args.RequiredString("task", _maxTask),
                new HashSet<string>(allowlist, StringComparer.Ordinal),
                args.RequiredString("model_id", _maxModel),
                ParseWorktree(args),
                args.OptionalStringArray("declared_paths", _maxPaths, _maxPath),
                args.OptionalStringArray("references", _maxReferences, _maxPath));
        }
        catch (CodingToolArgumentException ex)
        {
            return ex.ToOutput();
        }

        var parent = host.ParentFor(toolCall);
        if (parent is null)
        {
            return CodingToolResults.Failure("no_parent_context", "This session has no parent limits, so no subagent can start.");
        }

        if (!string.Equals(parent.ParentId, toolCall.SessionId, StringComparison.Ordinal))
        {
            // The parent limits must belong to the calling session; anything else would start the child under someone else's limits.
            return CodingToolResults.Failure("parent_mismatch", "The parent limits do not belong to this session, so no subagent started.");
        }

        SubagentHandle handle;
        try
        {
            handle = await spawner.SpawnAsync(parent, spec, cancellationToken).ConfigureAwait(false);
        }
        catch (SubagentRejectedException ex)
        {
            return CodingToolResults.Failure(
                "subagent_rejected_" + CodingToolResults.SnakeCase(ex.Reason.ToString()),
                "The spawn was refused by the subagent limits, so nothing started.");
        }

        SubagentResult result;
        try
        {
            result = await handle.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            handle.Cancel();
            throw;
        }

        var succeeded = result.Status == SubagentStatus.Completed;
        return new CodingToolOutput(new ExecOutcome(succeeded ? 0 : CodingToolResults.FailedExitCode), CodingToolResults.Json(w =>
        {
            w.WriteString("subagent_id", handle.Id);
            w.WriteString("status", CodingToolResults.SnakeCase(result.Status.ToString()));
            w.WriteString("summary", result.Summary.Value);
            w.WriteBoolean("summary_is_untrusted", true);
            w.WriteNumber("input_tokens", result.Usage.InputTokens);
            w.WriteNumber("output_tokens", result.Usage.OutputTokens);
            w.WriteNumber("steps", result.Usage.Steps);
            if (result.TraceId is not null)
            {
                w.WriteString("trace_id", result.TraceId);
            }
        }));
    }

    private static WorktreeRequest ParseWorktree(CodingToolArgs args) => args.OptionalString("worktree", 16) switch
    {
        null or "none" => WorktreeRequest.None,
        "read_only" => WorktreeRequest.ReadOnly,
        "own" => WorktreeRequest.Own,
        _ => throw new CodingToolArgumentException("worktree", "must be none, read_only or own"),
    };
}
