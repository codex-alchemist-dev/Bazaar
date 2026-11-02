namespace Bazaar.Server.Skimmer.Scoring;

/// <summary>One agent's scored result for a message. The score is in basis points, 0 to 10000.</summary>
public sealed record RelevanceResult
{
    /// <summary>Creates a result, rejecting scores outside 0 to 10000.</summary>
    public RelevanceResult(Guid AgentId, Category Category, int ScoreBasisPoints)
    {
        if (ScoreBasisPoints is < 0 or > 10000)
        {
            throw new ArgumentOutOfRangeException(nameof(ScoreBasisPoints), ScoreBasisPoints, "Score must be between 0 and 10000 basis points.");
        }

        this.AgentId = AgentId;
        this.Category = Category;
        this.ScoreBasisPoints = ScoreBasisPoints;
    }

    /// <summary>The agent this result is for.</summary>
    public Guid AgentId { get; }

    /// <summary>The category the scorer assigned.</summary>
    public Category Category { get; }

    /// <summary>Relevance in basis points, 0 to 10000.</summary>
    public int ScoreBasisPoints { get; }
}
