namespace Bazaar.Server.Skimmer.Scoring;

/// <summary>
/// Scores one message against one or more agents' subscriptions. The real implementation calls the model
/// through the gateway; this contract carries no gateway types. Implementations must return results that
/// pass <see cref="ScorerOutputParser"/>, or throw so the caller takes the fallback path.
/// </summary>
public interface IRelevanceScorer
{
    /// <summary>Scores the request. Throws on timeout (<see cref="OperationCanceledException"/>) or failure.</summary>
    Task<IReadOnlyList<RelevanceResult>> ScoreAsync(ScoreRequest request, CancellationToken ct);
}
