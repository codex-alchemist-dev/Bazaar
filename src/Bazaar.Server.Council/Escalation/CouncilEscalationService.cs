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
            ? ballots.Select(b => new EscalatedBallot(b.VoterId, b.Phase, b.Choice, b.Reasoning, b.TaintedAtWrite)).ToList()
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
            vote.DeadlineAt,
            revealed ? Summarise(options, ballots) : null,
            Flags(ballots));
    }

    private static TaintFlags Flags(IReadOnlyList<StoredBallot> ballots)
    {
        var tainted = ballots.Where(b => b.TaintedAtWrite).ToList();
        return new TaintFlags(tainted.Count, ballots.Count, [.. tainted.Select(b => b.VoterId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
    }

    private static DissentSummary Summarise(IReadOnlyList<string> options, IReadOnlyList<StoredBallot> ballots)
    {
        var independent = Count(options, ballots.Where(b => b.Phase == BallotPhase.Independent));
        var followUpBallots = ballots.Where(b => b.Phase == BallotPhase.FollowUp).ToList();
        var followUp = followUpBallots.Count == 0 ? [] : Count(options, followUpBallots);
        var latest = followUpBallots.Count == 0 ? independent : followUp;
        var contested = latest.Where(c => c.Choice != ApprovalChoices.Abstain).ToList();
        var total = contested.Sum(c => c.Count);
        var top = contested.Count == 0 ? 0 : contested.Max(c => c.Count);
        var leaders = contested.Where(c => c.Count == top && top > 0).ToList();
        var leading = leaders.Count == 1 ? leaders[0].Choice : null;
        var dissent = leading is null ? total : total - top;
        return new DissentSummary(independent, followUp, leading, dissent, total, total == 0 ? null : (double)dissent / total);
    }

    private static List<OptionCount> Count(IReadOnlyList<string> options, IEnumerable<StoredBallot> ballots)
    {
        var cast = ballots.ToList();
        var choices = options.Where(o => o != ApprovalChoices.Abstain).ToList();
        foreach (var extra in cast.Select(b => b.Choice).Where(c => c != ApprovalChoices.Abstain && !choices.Contains(c)).Distinct())
        {
            choices.Add(extra);
        }

        choices.Add(ApprovalChoices.Abstain);
        return [.. choices.Select(c => new OptionCount(c, cast.Count(b => b.Choice == c)))];
    }

    /// <summary>
    /// Resolves an escalated vote with a listed option (<see cref="ICouncilService.ResolveEscalationAsync"/>). The caller must
    /// already have checked that the principal is the owner.
    /// </summary>
    /// <exception cref="CouncilRejectedException">The vote is not escalated, or the choice is not one of its options.</exception>
    public Task ResolveAsync(Guid voteId, string choice, string? reasoning, CancellationToken ct) =>
        council.ResolveEscalationAsync(voteId, choice, reasoning, ct);
}
