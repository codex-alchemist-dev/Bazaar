using Bazaar.Server.Council.Service;

namespace Bazaar.Server.Council.Pilot;

/// <summary>Outcome label of a vote, taken from CI, a revert, or an owner accept (doc 10, Pilot metrics).</summary>
public enum OutcomeLabel
{
    /// <summary>The decided choice was right.</summary>
    Correct,

    /// <summary>The decided choice was wrong.</summary>
    Incorrect,
}

/// <summary>
/// One closed vote as the pilot report sees it. The independent choice is not an input: it is the reveal's top choice
/// computed from the independent ballots (<see cref="FlipClassifier.RevealTopChoice"/>).
/// </summary>
/// <param name="VoteId">The vote.</param>
/// <param name="Ballots">Both phases of the vote's ballots.</param>
/// <param name="TallyChoice">The choice the tally decided, or null when the vote escalated without a decisive result.</param>
/// <param name="OwnerResolution">The owner's resolution, or null when the owner did not resolve the vote.</param>
/// <param name="Outcome">The outcome label, or null when no outcome exists for the vote.</param>
/// <param name="CorrectChoice">The correct choice, when known. When null and <paramref name="Outcome"/> is Correct, the final choice is taken as correct. When null and the outcome is Incorrect, the vote is not comparable for accuracy.</param>
/// <param name="Tied">True when the vote ended in a tie.</param>
/// <param name="Tokens">Tokens used by the vote, or null when not measured.</param>
/// <param name="SubjectText">The subject text, used as citable evidence by the flip classifier.</param>
public sealed record PilotVote(
    Guid VoteId,
    IReadOnlyList<StoredBallot> Ballots,
    string? TallyChoice,
    string? OwnerResolution,
    OutcomeLabel? Outcome,
    string? CorrectChoice = null,
    bool Tied = false,
    int? Tokens = null,
    string? SubjectText = null)
{
    /// <summary>The decided choice: the owner's resolution when there is one, else the tally's choice.</summary>
    public string? FinalChoice => OwnerResolution ?? TallyChoice;

    /// <summary>True when the tally produced no decisive choice, so the vote went to escalation.</summary>
    public bool Escalated => TallyChoice is null;
}
