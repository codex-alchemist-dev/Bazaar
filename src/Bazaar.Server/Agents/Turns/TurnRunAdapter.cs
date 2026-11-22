using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Agents.Turns;

/// <summary>
/// Runs the <see cref="ITurnRunner"/> seam over the production <see cref="TurnRunner"/> (doc 06 WP5). The context is built
/// through <see cref="ITurnContextAssembler"/> after the turn row exists, so the manifest records the turn id.
/// <para>
/// A request that names inbox items claims exactly those items (pending and due ones; the rest are left alone). The tool
/// definitions in the catalogue named by the request's allowlist are offered to an actor turn; a reader turn offers none.
/// </para>
/// <para>
/// A tool call the model makes is dispatched by the runner through <see cref="IToolDispatcher"/>, and its result goes back to the
/// model on the next step. Not yet covered, and left out rather than approximated: recording the parent turn and depth. Those
/// follow in the next consolidation PRs.
/// </para>
/// </summary>
/// <param name="runner">The consolidated runner that runs the loop.</param>
/// <param name="assembler">Builds the context once the turn row exists.</param>
/// <param name="toolCatalogue">The tool definitions a turn may be offered. Empty by default, so no tools are offered.</param>
/// <param name="posts">Where a reply goes. Null posts nothing.</param>
/// <param name="replyTargets">Name the GC for a wake that has no trigger GC, such as a card assignment. A wake with a trigger GC is not posted here: that reply path belongs to the consolidated runner.</param>
public sealed class TurnRunAdapter(
    TurnRunner runner,
    ITurnContextAssembler assembler,
    IReadOnlyList<ModelToolDefinition>? toolCatalogue = null,
    IAgentPostSink? posts = null,
    IEnumerable<ITurnReplyTarget>? replyTargets = null) : ITurnRunner
{
    private readonly IAgentPostSink? _posts = posts;
    private readonly List<ITurnReplyTarget> _replyTargets = replyTargets?.ToList() ?? [];

    private readonly TurnRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    private readonly ITurnContextAssembler _assembler = assembler ?? throw new ArgumentNullException(nameof(assembler));
    private readonly IReadOnlyList<ModelToolDefinition> _toolCatalogue = toolCatalogue ?? [];

    /// <inheritdoc />
    public async Task<TurnRunOutcome> RunAsync(TurnRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await _runner.RunAsync(
            new TurnRequest(
                request.AgentId,
                request.SessionId,
                request.WakeReason,
                // Per-agent routing: the gateway resolves each agent's own policy default route (doc 05).
                string.Empty,
                Messages: [],
                Tools: TurnToolOffer.Select(request.Role, _toolCatalogue, request.ToolAllowlist),
                InboxClaimLimit: 0,
                MessageBuilder: (turnId, ct) => BuildMessagesAsync(request, turnId, ct),
                Role: request.Role,
                StepCapOverride: request.StepCapOverride,
                InboxItemIds: request.InboxItemIds.Count > 0 ? request.InboxItemIds : null,
                ChainDepth: request.ChainDepth,
                TriggerGcId: request.TriggerGcId),
            cancellationToken).ConfigureAwait(false);

        var reason = MapReason(result);
        if (reason == TurnEndReason.FinalMessage && result.TurnId is { } turnId)
            reason = await PostReplyAsync(request, turnId, result.FinalText, cancellationToken).ConfigureAwait(false);

        return new TurnRunOutcome(
            result.TurnId ?? Guid.Empty,
            reason,
            result.FinalText,
            result.ModelSteps);
    }

    /// <summary>
    /// Posts the final text where a reply-target component says (doc 06 step 5). Only a wake with no trigger GC is posted here. A
    /// guard refusal or a missing Soul binding leaves the turn completed and reports <see cref="TurnEndReason.PostRefused"/>.
    /// </summary>
    private async Task<TurnEndReason> PostReplyAsync(TurnRunRequest request, Guid turnId, string? text, CancellationToken ct)
    {
        if (_posts is null || string.IsNullOrWhiteSpace(text) || request.TriggerGcId is not null)
            return TurnEndReason.FinalMessage;

        foreach (var target in _replyTargets)
        {
            if (await target.ResolveGcAsync(request, ct).ConfigureAwait(false) is not { } gcId)
                continue;
            try
            {
                await _posts.PostAsync(request.AgentId, turnId, gcId.ToString("D"), text, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is Posting.AgentPostRefusedException or Posting.AgentPostUnboundException)
            {
                return TurnEndReason.PostRefused;
            }

            break;
        }

        return TurnEndReason.FinalMessage;
    }

    private async Task<IReadOnlyList<ModelMessage>> BuildMessagesAsync(
        TurnRunRequest request, Guid turnId, CancellationToken ct)
    {
        var plan = await _assembler.BuildAsync(
            new TurnContextRequest(request.AgentId, turnId, request.Role, request.WakeReason, request.InboxItemIds, request.TriggerGcId, request.TriggerGcSeq),
            ct).ConfigureAwait(false);
        return plan.Messages;
    }

    /// <summary>Maps the runner's outcome to the seam's end reason. A failure with no recorded reason is a gateway-side failure.</summary>
    private static TurnEndReason MapReason(TurnResult result) => result.Outcome switch
    {
        TurnOutcome.Completed => TurnEndReason.FinalMessage,
        TurnOutcome.NotAcquired => TurnEndReason.LeaseLost,
        _ => result.EndReason ?? TurnEndReason.GatewayFailed,
    };
}
