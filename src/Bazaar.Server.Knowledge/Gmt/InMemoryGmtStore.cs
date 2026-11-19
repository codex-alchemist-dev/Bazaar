using System.Collections.Concurrent;

namespace Bazaar.Server.Knowledge.Gmt;

/// <summary>
/// In-memory implementation of <see cref="IGmtStore"/> for fast headless testing and local operation.
/// </summary>
public sealed class InMemoryGmtStore : IGmtStore
{
    private readonly ConcurrentDictionary<Guid, GmtCandidate> _candidates = new();

    public Task StageCandidateAsync(GmtCandidate candidate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        _candidates[candidate.CandidateId] = candidate;
        return Task.CompletedTask;
    }

    public Task<GmtCandidate?> FindAsync(Guid candidateId, CancellationToken ct = default)
    {
        _candidates.TryGetValue(candidateId, out var candidate);
        return Task.FromResult(candidate);
    }

    public Task<IReadOnlyList<GmtCandidate>> ListStagedAsync(string? parentAgentId = null, CancellationToken ct = default)
    {
        var query = _candidates.Values.Where(c => c.Status == GmtCandidateStatus.Staged);

        if (!string.IsNullOrWhiteSpace(parentAgentId))
        {
            query = query.Where(c => c.ParentAgentId.Equals(parentAgentId, StringComparison.OrdinalIgnoreCase));
        }

        var result = query.OrderBy(c => c.CreatedAt).ToList();
        return Task.FromResult<IReadOnlyList<GmtCandidate>>(result);
    }

    public Task<IReadOnlyList<GmtCandidate>> ListActiveLessonsAsync(string parentAgentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentAgentId);

        // Crucial invariant (doc 25 §2, §3): Staged and Rejected candidates are strictly excluded
        var active = _candidates.Values
            .Where(c => c.ParentAgentId.Equals(parentAgentId, StringComparison.OrdinalIgnoreCase))
            .Where(c => c.Status is GmtCandidateStatus.Accepted or GmtCandidateStatus.Edited)
            .OrderBy(c => c.CreatedAt)
            .ToList();

        return Task.FromResult<IReadOnlyList<GmtCandidate>>(active);
    }

    public Task<bool> UpdateReviewAsync(
        Guid candidateId,
        GmtCandidateStatus status,
        string reviewer,
        DateTimeOffset reviewedAt,
        string? editedText = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewer);

        if (!_candidates.TryGetValue(candidateId, out var existing))
        {
            return Task.FromResult(false);
        }

        var updated = existing with
        {
            Status = status,
            ReviewedBy = reviewer,
            ReviewedAt = reviewedAt,
            EditedLessonText = editedText,
        };

        _candidates[candidateId] = updated;
        return Task.FromResult(true);
    }
}
