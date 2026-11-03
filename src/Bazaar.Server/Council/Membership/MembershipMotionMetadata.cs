namespace Bazaar.Server.Council.Membership;

/// <summary>What the mapper needs to know about a membership motion besides its tally.</summary>
/// <param name="VoteId">The Council vote.</param>
/// <param name="Kind">The motion kind.</param>
/// <param name="SubjectAgentId">The subject's agent id (the agent being hired, promoted, retired or changed).</param>
/// <param name="MotionClass">Ordinary or bigger change, fixed when the vote opened.</param>
/// <param name="DecidedAt">When the decision, owner resolution or withdrawal happened.</param>
public sealed record MembershipMotionMetadata(
    Guid VoteId,
    MembershipMotionKind Kind,
    string SubjectAgentId,
    MotionClass MotionClass,
    DateTimeOffset DecidedAt);
