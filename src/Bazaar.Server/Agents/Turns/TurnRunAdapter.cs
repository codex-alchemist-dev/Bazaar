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
/// Not yet covered, and left out rather than approximated: tool dispatch (a tool call still fails the turn, so offering
/// tools only helps when the model answers without calling one), and recording the parent turn and depth. Those follow in
/// the next consolidation PRs.
/// </para>
/// </summary>
/// <param name="runner">The consolidated runner that runs the loop.</param>
/// <param name="assembler">Builds the context once the turn row exists.</param>
/// <param name="route">The model route every turn uses.</param>
/// <param name="toolCatalogue">The tool definitions a turn may be offered. Empty by default, so no tools are offered.</param>
public sealed class TurnRunAdapter(
    TurnRunner runner,
    ITurnContextAssembler assembler,
    string route,
    IReadOnlyList<ModelToolDefinition>? toolCatalogue = null) : ITurnRunner
{
    private readonly TurnRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    private readonly ITurnContextAssembler _assembler = assembler ?? throw new ArgumentNullException(nameof(assembler));
    private readonly string _route = !string.IsNullOrWhiteSpace(route)
        ? route
        : throw new ArgumentException("A route is required.", nameof(route));
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
                _route,
                Messages: [],
                Tools: TurnToolOffer.Select(request.Role, _toolCatalogue, request.ToolAllowlist),
                InboxClaimLimit: 0,
                MessageBuilder: (turnId, ct) => BuildMessagesAsync(request, turnId, ct),
                Role: request.Role,
                StepCapOverride: request.StepCapOverride,
                InboxItemIds: request.InboxItemIds.Count > 0 ? request.InboxItemIds : null),
            cancellationToken).ConfigureAwait(false);

        return new TurnRunOutcome(
            result.TurnId ?? Guid.Empty,
            MapReason(result),
            result.FinalText,
            result.ModelSteps);
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
