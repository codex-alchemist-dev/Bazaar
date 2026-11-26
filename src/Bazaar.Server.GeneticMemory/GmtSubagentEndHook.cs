using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Subagents;
using Bazaar.Server.GeneticMemory.Abstractions;

namespace Bazaar.Server.GeneticMemory;

/// <summary>
/// The child-end hook of doc 06 for Genetic Memory Transfer (doc 25 work package 9): when a subagent child finishes its turn, its
/// proposed lessons are staged for review. Nothing is accepted here; staging only fills the review queue. A child that was
/// abandoned (lease lost or gateway failure) stages nothing, because its output is incomplete.
/// </summary>
public sealed class GmtSubagentEndHook(IGmtProposalSource source, IGeneticMemoryTransfer transfer) : ISubagentEndHook
{
    /// <inheritdoc />
    public async Task OnSubagentEndedAsync(SubagentEnded ended, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ended);
        if (ended.Result.Reason is TurnEndReason.LeaseLost or TurnEndReason.GatewayFailed)
        {
            return;
        }

        var proposals = await source.ReadAsync(ended, cancellationToken).ConfigureAwait(false);
        if (proposals is null)
        {
            return;
        }

        foreach (var proposal in proposals.Proposals)
        {
            // A refused proposal (secret, named person, quarantined child, duplicate) is dropped; the others still stage.
            await transfer.StageAsync(
                new GmtStageRequest(
                    proposals.ChildAgent, proposals.TargetAgent, proposal.ContentClass, proposal.Kind, proposal.Title, proposal.Body,
                    proposals.TraceRef, proposals.ChildTainted, proposals.ChildQuarantined, proposals.Terms),
                cancellationToken).ConfigureAwait(false);
        }
    }
}
