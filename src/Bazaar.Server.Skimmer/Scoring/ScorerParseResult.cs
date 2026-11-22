namespace Bazaar.Server.Skimmer.Scoring;

/// <summary>Outcome of parsing scorer output. <see cref="Results"/> is empty on failure and <see cref="Reason"/> empty on success.</summary>
public sealed record ScorerParseResult(bool Success, IReadOnlyList<RelevanceResult> Results, string Reason)
{
    /// <summary>A successful parse.</summary>
    public static ScorerParseResult Ok(IReadOnlyList<RelevanceResult> results) => new(true, results, string.Empty);

    /// <summary>A failed parse with the reason it failed.</summary>
    public static ScorerParseResult Fail(string reason) => new(false, Array.Empty<RelevanceResult>(), reason);
}
