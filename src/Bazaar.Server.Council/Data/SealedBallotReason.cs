using Bazaar.Server.Council.Service;

namespace Bazaar.Server.Council.Data;

/// <summary>
/// One stored ballot with its reasoning still sealed (doc 10, Seal once, copy ciphertext). Carries the choice, which stays clear,
/// and the sealed bytes exactly as stored; nothing here is plaintext reasoning.
/// </summary>
/// <param name="VoteId">The vote.</param>
/// <param name="VoterId">The voter's agent id.</param>
/// <param name="Phase">Independent or follow-up.</param>
/// <param name="Choice">The ballot's choice.</param>
/// <param name="TaintedAtWrite">Whether the voter's session was tainted when the ballot was written (doc 15).</param>
/// <param name="Reasoning">The sealed reasoning as stored.</param>
public sealed record SealedBallotReason(
    Guid VoteId,
    string VoterId,
    BallotPhase Phase,
    string Choice,
    bool TaintedAtWrite,
    ReasoningCiphertext Reasoning);
