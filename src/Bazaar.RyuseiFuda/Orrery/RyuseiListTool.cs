using System.Text.Json;
using Bazaar.Server.Agents.Turns.Tools;

namespace Bazaar.RyuseiFuda.Orrery;

/// <summary>
/// The <c>ryusei.list</c> agent tool (doc 38, WP-R9). Lists the triggers that name the calling agent as a target, optionally in
/// one state, through <see cref="RyuseiTriggerService.ListForTargetAsync"/>. Registered by
/// <see cref="RyuseiScheduleComponent"/> only when <c>Bazaar:Ryusei:ProjectId</c> is set.
/// </summary>
public sealed class RyuseiListTool(RyuseiTriggerService triggers, IRyuseiAgentDirectory directory) : IContextualTool
{
    private readonly RyuseiTriggerService _triggers = triggers ?? throw new ArgumentNullException(nameof(triggers));
    private readonly IRyuseiAgentDirectory _directory = directory ?? throw new ArgumentNullException(nameof(directory));

    /// <inheritdoc />
    public async Task<string> ExecuteAsync(JsonElement args, ToolCallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var parsed = RyuseiToolContract.ParseList(args);
        if (!parsed.IsValid)
        {
            return RyuseiToolContract.BuildRefusal(parsed.Refusal!).ToJsonString();
        }

        var lookup = await _directory.ResolveAsync(context.AgentId, Array.Empty<Guid>(), cancellationToken).ConfigureAwait(false);
        if (lookup.Caller is not { } caller)
        {
            return RyuseiToolCallers.UnknownCaller().ToJsonString();
        }

        var request = parsed.Request!;
        var records = await _triggers.ListForTargetAsync(caller.Id, request.State, request.Limit, cancellationToken).ConfigureAwait(false);
        return RyuseiToolContract.BuildList(records).ToJsonString();
    }
}
