namespace Bazaar.Server.Council;

/// <summary>A sealed ballot as the tally sees it. Reasoning is carried for the reveal and is not read here.</summary>
/// <param name="VoterId">The voter's agent id. Must be in the eligible electorate.</param>
/// <param name="Choice">An approve/reject choice (<see cref="ApprovalChoices"/>) or one of the vote's options.</param>
/// <param name="Reasoning">The stated reasoning shown at reveal.</param>
/// <param name="Phase">Independent or follow-up.</param>
public sealed record Ballot(string VoterId, string Choice, string Reasoning, BallotPhase Phase);
