using Bazaar.Server.Agents.Turns.Subagents;
using Bazaar.Server.GeneticMemory.Abstractions;

namespace Bazaar.Server.GeneticMemory;

/// <summary>
/// [PROPOSED] The proposal source for the <c>gmt.propose_lesson</c> tool: hands the end hook what the child recorded, with the
/// session facts from <see cref="IGmtChildContext"/>. Without a context, or for an unknown session, it returns nothing and the recorded
/// lessons are dropped (fail closed). The proposals are taken, so a child's lessons are staged at most once.
/// </summary>
public sealed class GmtRecordedProposalSource(GmtProposalStore store, IGmtChildContext? context = null) : IGmtProposalSource
{
    /// <inheritdoc />
    public async Task<GmtChildProposals?> ReadAsync(SubagentEnded ended, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ended);
        var proposals = store.Take(ended.Result.ChildTurnId);
        if (proposals.Count == 0 || context is null)
        {
            return null;
        }

        var resolved = await context.ResolveAsync(ended.Request.AgentId, ended.Request.SessionId, ct).ConfigureAwait(false);
        if (resolved is null)
        {
            return null;
        }

        var turn = ended.Result.ChildTurnId.ToString("N");
        return new GmtChildProposals(
            "subagent-" + turn, resolved.TargetAgent, "turn/" + turn, resolved.Tainted, resolved.Quarantined, resolved.Terms, proposals);
    }
}
