using System.Text.Json;
using System.Text.Json.Nodes;
using Bazaar.Server.Agents.Turns.Tools;

namespace Bazaar.RyuseiFuda.Orrery;

/// <summary>
/// The <c>ryusei.schedule</c> agent tool (doc 38, WP-R9): parses the arguments, resolves the caller and the targets from the
/// agent directory, and hands the request to <see cref="RyuseiTriggerService.CreateAsync"/>. Authority, caps and predicate
/// checks stay in the service and its rules; this adapter adds none.
/// </summary>
/// <remarks>
/// Known gaps, to close before this tool is registered for agents:
/// <list type="bullet">
/// <item>Chain depth is the caller's context depth plus one. A trigger created here is one fire deeper than the turn that
/// created it. Above the maximum, the trigger rules refuse the create (<c>ChainDepthExceeded</c>). The woken turn's depth
/// reaches <see cref="ToolCallContext"/> only once the turn starter sets <see cref="TurnRunRequest.ChainDepth"/> from its
/// fire, and that starter is not in the codebase yet.</item>
/// <item>The interval and zone are UTC (zone null). Per-agent timezones are not on the agent row yet.</item>
/// <item>Not registered at startup. No <see cref="ToolRegistry"/> is built at startup, so there is nothing to register into.
/// Registration is a follow-up for the dispatcher wiring.</item>
/// </list>
/// </remarks>
public sealed class RyuseiScheduleTool(
    RyuseiTriggerService triggers,
    IRyuseiAgentDirectory directory,
    TimeProvider time,
    RyuseiCoworkerTriggers coworkerTriggers = RyuseiCoworkerTriggers.Allowed) : IContextualTool
{
    private readonly RyuseiTriggerService _triggers = triggers ?? throw new ArgumentNullException(nameof(triggers));
    private readonly IRyuseiAgentDirectory _directory = directory ?? throw new ArgumentNullException(nameof(directory));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly RyuseiCoworkerTriggers _coworkerTriggers = coworkerTriggers;

    /// <inheritdoc />
    public async Task<string> ExecuteAsync(JsonElement args, ToolCallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var parsed = RyuseiToolContract.ParseSchedule(args);
        if (!parsed.IsValid)
        {
            return RyuseiToolContract.BuildRefusal(parsed.Refusal!).ToJsonString();
        }

        var request = parsed.Request!;
        var lookup = await _directory.ResolveAsync(context.AgentId, request.TargetAgentIds, cancellationToken).ConfigureAwait(false);
        if (lookup.Caller is not { } caller || caller.ProjectId is not { } projectId)
        {
            return RyuseiToolCallers.UnknownCaller().ToJsonString();
        }

        var targets = lookup.TargetsFor(request.TargetAgentIds);
        if (targets is null)
        {
            return RyuseiToolCallers.UnknownTarget().ToJsonString();
        }

        var result = await _triggers.CreateAsync(
            caller,
            projectId,
            targets,
            request.Kind,
            request.Spec,
            request.Purpose,
            taskId: null,
            attachGcId: request.AttachGcId,
            pingMode: request.PingMode,
            chainDepth: checked(context.ChainDepth + 1),
            now: _time.GetUtcNow(),
            zone: null,
            coworker: _coworkerTriggers,
            ct: cancellationToken).ConfigureAwait(false);

        return RyuseiToolContract.BuildResult(result).ToJsonString();
    }
}
