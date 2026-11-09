using System.Text.Json;
using Bazaar.Server.Agents.Turns.Tools;

namespace Bazaar.Server.Kanban.Ryusei;

/// <summary>
/// The <c>ryusei.pause</c> agent tool (doc 38, WP-R9). Resolves the caller, then calls
/// <see cref="RyuseiTriggerService.PauseAsync"/>, which applies the authority rule and writes the audit row. Not registered at
/// startup; see <see cref="RyuseiScheduleTool"/> for the registration follow-up.
/// </summary>
public sealed class RyuseiPauseTool(RyuseiTriggerService triggers, IRyuseiAgentDirectory directory, TimeProvider time) : IContextualTool
{
    private readonly RyuseiTriggerService _triggers = triggers ?? throw new ArgumentNullException(nameof(triggers));
    private readonly IRyuseiAgentDirectory _directory = directory ?? throw new ArgumentNullException(nameof(directory));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));

    /// <inheritdoc />
    public async Task<string> ExecuteAsync(JsonElement args, ToolCallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var parsed = RyuseiToolContract.ParseTriggerAction(args);
        if (!parsed.IsValid)
        {
            return RyuseiToolContract.BuildRefusal(parsed.Refusal!).ToJsonString();
        }

        var lookup = await _directory.ResolveAsync(context.AgentId, Array.Empty<Guid>(), cancellationToken).ConfigureAwait(false);
        if (lookup.Caller is not { } caller)
        {
            return RyuseiToolCallers.UnknownCaller().ToJsonString();
        }

        var result = await _triggers.PauseAsync(caller, parsed.Request!.TriggerId, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return RyuseiToolContract.BuildResult(result).ToJsonString();
    }
}
