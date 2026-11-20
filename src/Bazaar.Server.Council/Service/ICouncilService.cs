namespace Bazaar.Server.Council.Service;

/// <summary>
/// A motion to open (docs/design/10-council-and-voting.md, Steps 1 and Membership and role votes).
/// </summary>
/// <param name="SubjectRef">What the vote is about (a task, a hire candidate, a Constitution clause, and so on).</param>
/// <param name="Kind">Ballot shape.</param>
/// <param name="MotionClass">Ordinary or bigger change.</param>
/// <param name="Options">Option ids for option choice (2 to 6, distinct); empty for approve/reject.</param>
/// <param name="GcId">The GC the vote runs in. The electorate is its agent members.</param>
/// <param name="ProposerId">The agent opening the vote. Cannot vote on it.</param>
/// <param name="SubjectAuthorId">The author of the subject. Excluded from voting when council.exclude_author is set.</param>
/// <param name="Deadline">The requested deadline. The service caps it at council.vote_deadline_cap_hours from open.</param>
/// <param name="MembershipSubjectId">For membership votes (hire, promote, retire), the agent the vote is about. Excluded
/// from voting when council.exclude_membership_subject is set. Null for other motions.</param>
public sealed record MotionDraft(
    string SubjectRef,
    VoteKind Kind,
    MotionClass MotionClass,
    IReadOnlyList<string> Options,
    Guid GcId,
    string ProposerId,
    string? SubjectAuthorId,
    DateTimeOffset Deadline,
    string? MembershipSubjectId = null);

/// <summary>
/// Council operations (docs/design/10-council-and-voting.md, Data and interfaces). Vote ids are Guids, generated at open.
/// Authorisation beyond the rules named on each method (who may resolve an escalation, for example) belongs to the
/// caller that exposes this service.
/// </summary>
public interface ICouncilService
{
    /// <summary>Opens a vote. Only the proposer may open it. Snapshots the electorate and returns the vote id.</summary>
    /// <exception cref="ArgumentException">The options do not fit the vote kind.</exception>
    /// <exception cref="CouncilRejectedException">The caller is not the proposer, or the deadline has passed.</exception>
    Task<Guid> OpenAsync(string callerId, MotionDraft motion, CancellationToken ct);

    /// <summary>Records one ballot (independent or follow-up). Triggers the reveal or the follow-up tally when complete.</summary>
    /// <exception cref="CouncilRejectedException">The ballot is not accepted: wrong state, not eligible, bad choice or reasoning, or a duplicate.</exception>
    Task SubmitBallotAsync(Guid voteId, string voterId, Ballot ballot, CancellationToken ct);

    /// <summary>The owner resolves an escalated vote with a listed option (or yes or no for approve/reject).</summary>
    /// <exception cref="CouncilRejectedException">The vote is not escalated, or the choice is not one of its options.</exception>
    Task ResolveEscalationAsync(Guid voteId, string optionChoice, string? reasoning, CancellationToken ct);

    /// <summary>Applies the deadline rules to a vote whose deadline has passed. Does nothing before the deadline.</summary>
    Task ProcessDeadlineAsync(Guid voteId, CancellationToken ct);

    /// <summary>Withdraws a vote. Allowed for the proposer or the owner, before the vote is decided.</summary>
    /// <exception cref="CouncilRejectedException">The actor may not withdraw, or the vote is already terminal.</exception>
    Task WithdrawAsync(Guid voteId, string actorId, CancellationToken ct);
}
