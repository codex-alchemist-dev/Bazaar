namespace Bazaar.Server.Council.Prompts;

/// <summary>
/// Input to <see cref="BallotPromptBuilder"/>. The typed shape is the first privacy line: an independent request has no
/// field for revealed reasons, and a follow-up request has no field for peers' ballots other than the revealed set.
/// </summary>
/// <param name="Motion">The motion being voted on.</param>
/// <param name="VoterId">The voter's own agent id. Used for validation only; never written into the prompt.</param>
/// <param name="Phase">Independent (sealed first ballot) or follow-up (after the reveal).</param>
/// <param name="Context">Working or Thinking GC.</param>
/// <param name="TaintedAtWrite">Whether the voter's session is tainted; passed through to <see cref="BallotPrompt"/> for recording.</param>
/// <param name="OtherVoterIds">Ids of the other eligible voters. The guard denies any prompt that contains one.</param>
/// <param name="OwnIndependentBallot">The voter's own independent ballot. Required for follow-up; must be null for independent.</param>
/// <param name="Revealed">The revealed reasons in stored order. Required for follow-up; must be null or empty for independent.</param>
/// <param name="Rules">Reasoning length guidance.</param>
public sealed record BallotPromptRequest(
    CouncilMotion Motion,
    string VoterId,
    BallotPhase Phase,
    PromptContext Context,
    bool TaintedAtWrite,
    IReadOnlyCollection<string> OtherVoterIds,
    Ballot? OwnIndependentBallot = null,
    IReadOnlyList<RevealedReason>? Revealed = null,
    BallotPromptRules? Rules = null);
