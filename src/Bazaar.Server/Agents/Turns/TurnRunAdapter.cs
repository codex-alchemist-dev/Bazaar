using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Agents.Turns;

/// <summary>
/// Runs the <see cref="ITurnRunner"/> seam over the production <see cref="TurnRunner"/> (doc 06 WP5). The context is built
/// through <see cref="ITurnContextAssembler"/> after the turn row exists, so the manifest records the turn id.
/// <para>
/// Not yet covered, and rejected or left out rather than approximated: claiming specific inbox items (the runner claims
/// by count only, so a request that names items throws), tool dispatch (a tool call still fails the turn, and no tools are
/// offered to the model), and recording the parent turn and depth. Those follow in the next consolidation PRs.
/// </para>
/// </summary>
public sealed class TurnRunAdapter(TurnRunner runner, ITurnContextAssembler assembler, string route) : ITurnRunner
{
    private readonly TurnRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    private readonly ITurnContextAssembler _assembler = assembler ?? throw new ArgumentNullException(nameof(assembler));
    private readonly string _route = !string.IsNullOrWhiteSpace(route)
        ? route
        : throw new ArgumentException("A route is required.", nameof(route));

    /// <inheritdoc />
    public async Task<TurnRunOutcome> RunAsync(TurnRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.InboxItemIds.Count > 0)
            throw new NotSupportedException("Claiming specific inbox items is not supported by the consolidated runner yet.");

        var result = await _runner.RunAsync(
            new TurnRequest(
                request.AgentId,
                request.SessionId,
                request.WakeReason,
                _route,
                Messages: [],
                InboxClaimLimit: 0,
                MessageBuilder: (turnId, ct) => BuildMessagesAsync(request, turnId, ct),
                Role: request.Role,
                StepCapOverride: request.StepCapOverride),
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
