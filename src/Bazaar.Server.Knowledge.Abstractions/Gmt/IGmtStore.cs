namespace Bazaar.Server.Knowledge.Gmt;

/// <summary>
/// Persistence interface for Genetic Memory Transfer candidate staging and review (doc 25 §2, WP 9).
/// </summary>
public interface IGmtStore
{
    /// <summary>Stages a new candidate lesson.</summary>
    Task StageCandidateAsync(GmtCandidate candidate, CancellationToken ct = default);

    /// <summary>Finds a candidate lesson by its ID.</summary>
    Task<GmtCandidate?> FindAsync(Guid candidateId, CancellationToken ct = default);

    /// <summary>Lists candidates currently staged in the review queue, optionally filtered by parent agent.</summary>
    Task<IReadOnlyList<GmtCandidate>> ListStagedAsync(string? parentAgentId = null, CancellationToken ct = default);

    /// <summary>Lists all accepted or edited lessons eligible for retrieval by the parent agent.</summary>
    Task<IReadOnlyList<GmtCandidate>> ListActiveLessonsAsync(string parentAgentId, CancellationToken ct = default);

    /// <summary>Updates candidate review decision.</summary>
    Task<bool> UpdateReviewAsync(
        Guid candidateId,
        GmtCandidateStatus status,
        string reviewer,
        DateTimeOffset reviewedAt,
        string? editedText = null,
        CancellationToken ct = default);
}
