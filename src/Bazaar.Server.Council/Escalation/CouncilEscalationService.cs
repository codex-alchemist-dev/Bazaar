using Bazaar.Server.Council.Service;

namespace Bazaar.Server.Council.Escalation;

/// <summary>
/// The owner's escalation surface (docs/design/10-council-and-voting.md, Escalation): list the escalated votes awaiting the
/// owner, and resolve one. Authorisation is the caller's (the HTTP route admits only the owner principal), as for
/// <see cref="ICouncilService.ResolveEscalationAsync"/>.
/// </summary>
public sealed class CouncilEscalationService(ICouncilEscalationSource queue, ICouncilStore store, ICouncilService council)
{
    private const int _maxListed = 200;

    /// <summary>Lists the votes awaiting the owner, oldest escalation first.</summary>
    public async Task<IReadOnlyList<EscalatedVote>> ListAwaitingAsync(CancellationToken ct) =>
        await ListAwaitingAsync(_maxListed, ct).ConfigureAwait(false);

    /// <summary>Lists at most <paramref name="limit"/> votes awaiting the owner, oldest escalation first.</summary>
    public async Task<IReadOnlyList<EscalatedVote>> ListAwaitingAsync(int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var ids = await queue.ListEscalatedAsync(limit, ct).ConfigureAwait(false);
        var result = new List<EscalatedVote>(ids.Count);
        foreach (var id in ids)
        {
            var view = await ViewAsync(id, ct).ConfigureAwait(false);
            if (view is not null)
            {
                result.Add(view);
            }
        }

        return result;
    }

    /// <summary>Reads one vote's owner view, or null when it does not exist or is no longer Escalated.</summary>
    public async Task<EscalatedVote?> ViewAsync(Guid voteId, CancellationToken ct)
    {
        var vote = await store.FindVoteAsync(voteId, ct).ConfigureAwait(false);
        if (vote is null || vote.State != VoteState.Escalated)
        {
            return null;
        }

        var history = await store.ListTransitionsAsync(voteId, ct).ConfigureAwait(false);
        var escalated = history.LastOrDefault(t => t.To == VoteState.Escalated);
        var revealed = history.Any(t => t.To == VoteState.Revealed);
        var ballots = await store.ListBallotsAsync(voteId, ct).ConfigureAwait(false);
        var shown = revealed
            ? ballots.Select(b => new EscalatedBallot(b.VoterId, b.Phase, b.Choice, b.Reasoning)).ToList()
            : [];
        IReadOnlyList<string> options = vote.Kind == VoteKind.ApproveReject
            ? [ApprovalChoices.Yes, ApprovalChoices.No]
            : vote.Options;
        return new EscalatedVote(
            vote.VoteId,
            vote.GcId,
            vote.SubjectRef,
            vote.Kind,
            vote.MotionClass,
            options,
            vote.EligibleVoterIds.Count,
            ballots.Count(b => b.Phase == BallotPhase.Independent),
            revealed,
            shown,
            escalated?.Reason,
            escalated?.At ?? vote.CreatedAt,
            vote.DeadlineAt);
    }

    /// <summary>
    /// Resolves an escalated vote with a listed option (<see cref="ICouncilService.ResolveEscalationAsync"/>). The caller must
    /// already have checked that the principal is the owner.
    /// </summary>
    /// <exception cref="CouncilRejectedException">The vote is not escalated, or the choice is not one of its options.</exception>
    public Task ResolveAsync(Guid voteId, string choice, string? reasoning, CancellationToken ct) =>
        council.ResolveEscalationAsync(voteId, choice, reasoning, ct);
}
