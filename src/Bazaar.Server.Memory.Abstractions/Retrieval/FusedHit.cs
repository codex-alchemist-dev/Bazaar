namespace Bazaar.Server.Memory.Retrieval;

/// <summary>
/// A fused retrieval result with its labels.
/// </summary>
/// <param name="Id">Candidate id.</param>
/// <param name="Score">Sum over the lists that contain the id of 1 / (k + rank), rank starting at 1.</param>
/// <param name="FromLexical">True when the lexical (full-text or BM25) list contains the id.</param>
/// <param name="FromVector">True when the vector (embedding) list contains the id.</param>
/// <param name="LexicalOnly">True when the vector list was empty, so embeddings were absent for this pass.
/// Doc 08 requires the response to say so.</param>
public sealed record FusedHit(
    string Id,
    double Score,
    bool FromLexical,
    bool FromVector,
    bool LexicalOnly);
