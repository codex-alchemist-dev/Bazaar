using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Coding;
using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingPermissions;

namespace Bazaar.Server.CodingRunner;

/// <summary>
/// The guarded tool body under the Agents turn loop (doc 17, WP15). It is not an agent loop: it takes one tool call,
/// asks the gate through <see cref="IToolCallRecorder"/>, runs the registered handler only when the scope may run,
/// serialises writers per worktree, and always completes the trace row. Model-caused problems return a structured result.
/// </summary>
public sealed class CodingToolRunner : ICodingToolRunner
{
    /// <summary>Exit code recorded for an unknown tool (shell convention: command not found).</summary>
    public const int UnknownToolExitCode = 127;

    /// <summary>Exit code recorded when a writer call has no worktree key (usage error).</summary>
    public const int MissingWorktreeExitCode = 64;

    /// <summary>Exit code recorded when the worktree stayed busy (temporary failure).</summary>
    public const int BusyExitCode = 75;

    /// <summary>Exit code recorded when a handler threw.</summary>
    public const int HandlerFailedExitCode = -1;

    private readonly FrozenDictionary<string, ICodingToolHandler> _handlers;
    private readonly IToolCallRecorder _recorder;
    private readonly IWorktreeLocks _locks;
    private readonly CodingToolRunnerOptions _options;
    private readonly TimeProvider _time;

    /// <summary>Creates the runner. Handler names must be unique and non-blank.</summary>
    public CodingToolRunner(
        IEnumerable<ICodingToolHandler> handlers,
        IToolCallRecorder recorder,
        IWorktreeLocks locks,
        CodingToolRunnerOptions? options = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(locks);
        var map = new Dictionary<string, ICodingToolHandler>(StringComparer.Ordinal);
        foreach (var handler in handlers)
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (string.IsNullOrWhiteSpace(handler.Name) || handler.Name != handler.Name.Trim())
            {
                throw new ArgumentException("Tool names must be non-blank and must not have surrounding whitespace.", nameof(handlers));
            }

            if (!map.TryAdd(handler.Name, handler))
            {
                throw new ArgumentException($"Tool '{handler.Name}' is registered twice.", nameof(handlers));
            }
        }

        _handlers = map.ToFrozenDictionary(StringComparer.Ordinal);
        _recorder = recorder;
        _locks = locks;
        _options = options ?? new CodingToolRunnerOptions();
        _options.Validate();
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<CodingToolResult> RunAsync(CodingToolCall toolCall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        cancellationToken.ThrowIfCancellationRequested();

        var known = _handlers.TryGetValue(toolCall.Tool ?? string.Empty, out var handler);

        // An unknown tool is still traced, as the most restrictive class, so the attempt is visible.
        var capability = known ? handler!.CapabilityClass : ToolCapabilityClass.Exec;
        var args = toolCall.Args.ValueKind == JsonValueKind.Undefined ? _emptyObject : toolCall.Args;
        var context = new ToolCallContext(
            toolCall.AgentId,
            toolCall.SessionId,
            toolCall.Tool ?? string.Empty,
            capability,
            Sha256Hex(args.GetRawText()),
            toolCall.SourceTrust,
            toolCall.Taint,
            toolCall.TargetPath,
            toolCall.TargetBranch,
            args,
            toolCall.Role,
            toolCall.Grant);

        ToolCallScope scope;
        try
        {
            scope = await _recorder.BeginAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return Error(CodingToolStatus.Error, "gate_failed", "The call could not be judged, so it did not run.", null);
        }

        if (!scope.MayRun)
        {
            return Refusal(scope);
        }

        if (!known)
        {
            var completion = await CompleteSafeAsync(scope, new ExecOutcome(UnknownToolExitCode)).ConfigureAwait(false);
            return Error(CodingToolStatus.UnknownTool, "unknown_tool", $"No tool named '{Truncate(toolCall.Tool)}' is available.", scope.CallId, UnknownToolExitCode, completion.TraceFailed);
        }

        IWorktreeLease? lease = null;
        try
        {
            if (handler!.WritesWorktree)
            {
                if (string.IsNullOrWhiteSpace(toolCall.WorktreeKey))
                {
                    var completion = await CompleteSafeAsync(scope, new ExecOutcome(MissingWorktreeExitCode)).ConfigureAwait(false);
                    return Error(CodingToolStatus.Error, "worktree_required", "This tool changes a worktree and the call names none.", scope.CallId, MissingWorktreeExitCode, completion.TraceFailed);
                }

                lease = await AcquireAsync(toolCall.WorktreeKey, cancellationToken).ConfigureAwait(false);
                if (lease is null)
                {
                    var completion = await CompleteSafeAsync(scope, new ExecOutcome(BusyExitCode)).ConfigureAwait(false);
                    return Error(CodingToolStatus.Busy, "worktree_busy", "Another writer holds this worktree. Retry later.", scope.CallId, BusyExitCode, completion.TraceFailed);
                }
            }

            return await ExecuteAsync(handler, toolCall, scope, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Lock wait cancelled before the handler ran: the row is open and must not stay so.
            if (!scope.IsCompleted)
            {
                await CompleteSafeAsync(scope, new ExecOutcome(null, Cancelled: true)).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<CodingToolResult> ExecuteAsync(ICodingToolHandler handler, CodingToolCall toolCall, ToolCallScope scope, CancellationToken cancellationToken)
    {
        CodingToolOutput output;
        try
        {
            output = await handler.ExecuteAsync(toolCall, cancellationToken).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(output);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CompleteSafeAsync(scope, new ExecOutcome(null, Cancelled: true)).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            // The handler's own timeout, not the caller's cancel.
            var completion = await CompleteSafeAsync(scope, new ExecOutcome(null, TimedOut: true)).ConfigureAwait(false);
            return Error(CodingToolStatus.TimedOut, "timed_out", "The tool timed out.", scope.CallId, null, completion.TraceFailed);
        }
        catch (Exception ex)
        {
            var completion = await CompleteSafeAsync(scope, new ExecOutcome(HandlerFailedExitCode)).ConfigureAwait(false);
            return Error(CodingToolStatus.Failed, "tool_failed", $"The tool failed ({ex.GetType().Name}).", scope.CallId, HandlerFailedExitCode, completion.TraceFailed);
        }

        var done = await CompleteSafeAsync(scope, output.Outcome).ConfigureAwait(false);
        var status = done.Status switch
        {
            ToolCallStatus.Succeeded => CodingToolStatus.Ok,
            ToolCallStatus.Failed => CodingToolStatus.Failed,
            ToolCallStatus.TimedOut => CodingToolStatus.TimedOut,
            _ => CodingToolStatus.Unknown,
        };
        return new CodingToolResult(status, output.Outcome.ExitCode, output.Output, CallId: scope.CallId, TraceFailed: done.TraceFailed);
    }

    private async ValueTask<IWorktreeLease?> AcquireAsync(string key, CancellationToken cancellationToken)
    {
        var deadline = _time.GetUtcNow() + _options.LockWaitTimeout;
        while (true)
        {
            var lease = await _locks.TryAcquireAsync(key, _options.LockLeaseDuration, cancellationToken).ConfigureAwait(false);
            if (lease is not null)
            {
                return lease;
            }

            if (_time.GetUtcNow() >= deadline)
            {
                return null;
            }

            await Task.Delay(_options.LockPollInterval, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    // Always runs with no cancellation token so a cancelled call still closes its row. A store failure is reported, not thrown.
    private async ValueTask<ToolCallCompletion> CompleteSafeAsync(ToolCallScope scope, ExecOutcome outcome)
    {
        try
        {
            return await _recorder.CompleteAsync(scope, outcome, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return new ToolCallCompletion(MapStatus(outcome), false, true, false);
        }
    }

    // Same mapping as the recorder (doc 17): only exit 0 without timeout or cancel is a success.
    private static ToolCallStatus MapStatus(ExecOutcome o) =>
        o.Crashed ? ToolCallStatus.Unknown
        : o.TimedOut ? ToolCallStatus.TimedOut
        : o.Cancelled ? ToolCallStatus.Failed
        : o.ExitCode switch { 0 => ToolCallStatus.Succeeded, null => ToolCallStatus.Unknown, _ => ToolCallStatus.Failed };

    private static CodingToolResult Refusal(ToolCallScope scope)
    {
        var d = scope.Decision;
        if (d.Outcome == PermissionOutcome.RequireApproval)
        {
            var message = d.Scope is null ? d.Reason : $"{d.Reason} Approval needed: {d.Scope.Description}";
            return Error(CodingToolStatus.ApprovalRequired, "approval_required", message, scope.CallId);
        }

        if (d.Outcome == PermissionOutcome.Allow)
        {
            // Allowed but no trace row: the gate could not record it, so nothing runs.
            return Error(CodingToolStatus.Error, "trace_unavailable", "The call could not be recorded, so it did not run.", scope.CallId);
        }

        return Error(CodingToolStatus.Denied, "denied", d.Reason, scope.CallId);
    }

    private static CodingToolResult Error(CodingToolStatus status, string code, string message, Guid? callId, int? exitCode = null, bool traceFailed = false) =>
        new(status, exitCode, ErrorJson(code, message), code, message, callId, traceFailed);

    private static string ErrorJson(string code, string message)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteStartObject("error");
            w.WriteString("code", code);
            w.WriteString("message", message);
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Truncate(string? value) =>
        value is null ? string.Empty : value.Length <= 64 ? value : value[..64];

    private static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static readonly JsonElement _emptyObject = JsonElement.Parse("{}");
}
