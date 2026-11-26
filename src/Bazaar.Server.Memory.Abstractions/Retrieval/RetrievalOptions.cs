namespace Bazaar.Server.Memory.Retrieval;

/// <summary>
/// Settings for one retrieval pass (doc 08, section 8). Doc 08 gives no numbers for top-k or the token budget,
/// so both are required of the caller. The RRF constant comes from doc 18 (`vault.search.rrf_k`, shipped
/// default 60); the caller may pass another value once that setting is wired.
/// </summary>
/// <param name="TokenBudget">Total tokens the packed hits may use. Zero or more.</param>
/// <param name="TopK">Maximum number of fused hits kept before budget packing. Zero or more.</param>
/// <param name="RrfK">Reciprocal rank fusion constant. Must be at least 1.</param>
public sealed record RetrievalOptions(int TokenBudget, int TopK, int RrfK = 60);
