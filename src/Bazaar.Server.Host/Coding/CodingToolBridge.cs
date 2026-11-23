using System.Buffers;
using System.Text.Json;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.CodingPermissions;
using Bazaar.Server.CodingRunner;
using Bazaar.Server.Security.Taint;
using ModuleSessionTaint = Bazaar.Server.CodingPermissions.SessionTaint;
using ServerToolCallContext = Bazaar.Server.Agents.Turns.Tools.ToolCallContext;

namespace Bazaar.Server.Host.Coding;

/// <summary>
/// One coding tool as the Agents tool dispatcher sees it. The dispatcher has already applied doc 15 policy (reader turns,
/// capability, taint tier); this bridge adds the coding runner's own gate, worktree lock and trace row by handing the call to
/// <see cref="ICodingToolRunner"/>. Everything the runner treats as trusted comes from the host, never from the model:
/// the agent and session from the dispatcher's <see cref="ServerToolCallContext"/>, the taint from the session store, and the
/// worktree key from the session's task worktree (one worktree per session).
/// </summary>
public sealed class CodingToolBridge(string toolName, ICodingToolRunner runner, ISessionTaintStore taint, ICodingSessionWorktreeKeys worktreeKeys) : IContextualTool
{
    /// <summary>The tool name this bridge runs.</summary>
    public string ToolName { get; } = !string.IsNullOrWhiteSpace(toolName) ? toolName : throw new ArgumentException("A tool name is required.", nameof(toolName));

    /// <inheritdoc />
    public async Task<string> ExecuteAsync(JsonElement args, ServerToolCallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sessionTaint = await taint.LoadAsync(context.SessionId, cancellationToken).ConfigureAwait(false);
        var sessionId = context.SessionId.ToString("D");
        var call = new CodingToolCall(
            AgentId: context.AgentId.ToString("D"),
            SessionId: sessionId,
            Tool: ToolName,
            Args: args,
            // A hard-tainted session is acting on content it did not write, so its calls carry the external trust label.
            SourceTrust: sessionTaint.State == TaintState.Hard ? "external" : "agent",
            Taint: MapTaint(sessionTaint.State),
            // The dispatcher refuses reader turns for every tool but an operator-listed read tool, so a call that reaches the
            // bridge is an actor call or a listed read-only one.
            Role: ToolSessionRole.Actor,
            // The key the task workspace issued for this session. A session with none gets its own id, which no worktree is held under, so a worktree tool fails with a usage error.
            WorktreeKey: worktreeKeys.KeyFor(context.SessionId) ?? sessionId,
            TargetPath: StringArg(args, "path"),
            TargetBranch: StringArg(args, "branch"),
            Grant: null);

        var result = await runner.RunAsync(call, cancellationToken).ConfigureAwait(false);
        return Render(result);
    }

    /// <summary>The structured result the model sees: status first, then the handler's JSON (embedded as JSON when it is valid).</summary>
    internal static string Render(CodingToolResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("status", result.Status.ToString().ToLowerInvariant());
            if (result.ExitCode is { } exit)
            {
                w.WriteNumber("exit_code", exit);
            }

            if (result.ErrorCode is { } code)
            {
                w.WriteString("error_code", code);
            }

            if (result.Message is { } message)
            {
                w.WriteString("message", message);
            }

            w.WritePropertyName("output");
            if (IsJson(result.Output))
            {
                w.WriteRawValue(result.Output);
            }
            else
            {
                w.WriteStringValue(result.Output);
            }

            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static ModuleSessionTaint MapTaint(TaintState state) => state switch
    {
        TaintState.Clean => ModuleSessionTaint.None,
        TaintState.Soft => ModuleSessionTaint.Soft,
        // Hard, and any level this mapping does not know, so a new level fails closed.
        _ => ModuleSessionTaint.Hard,
    };

    private static string? StringArg(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
