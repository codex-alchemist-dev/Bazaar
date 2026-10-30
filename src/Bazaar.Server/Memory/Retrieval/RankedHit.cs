namespace Bazaar.Server.Memory.Retrieval;

/// <summary>
/// One candidate from a search list. Lists are passed in rank order, best first; fusion uses the position
/// (rank), not the score. The score is carried for callers and is not used by fusion.
/// </summary>
/// <param name="Id">Candidate id (a note or block id). Must not be empty.</param>
/// <param name="Score">The engine's own score for this candidate, informational only.</param>
public sealed record RankedHit(string Id, double Score);
