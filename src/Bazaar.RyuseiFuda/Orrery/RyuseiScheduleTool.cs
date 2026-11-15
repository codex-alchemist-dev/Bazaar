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
/// Chain depth is the caller's context depth plus one, so a trigger created by a woken turn is one fire deeper than that turn.
/// The depth of a woken turn reaches <see cref="ToolCallContext"/> from the turn starter (doc 38, Chain depth). Above the
/// maximum, the trigger rules refuse the create (<c>ChainDepthExceeded</c>).
/// The interval and zone are UTC (zone null). Per-agent timezones are not on the agent row yet.
/// The tool is registered by <see cref="RyuseiScheduleComponent"/> only when <c>Bazaar:Ryusei:ProjectId</c> is set.
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
