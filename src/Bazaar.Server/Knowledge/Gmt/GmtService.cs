using Bazaar.Server.Memory.Trust;

namespace Bazaar.Server.Knowledge.Gmt;

/// <summary>Result of reviewing a GMT candidate lesson.</summary>
public sealed record GmtReviewResult(
    bool Succeeded,
    string? Error = null,
    GmtCandidate? Candidate = null);

/// <summary>
/// Service managing Genetic Memory Transfer (GMT) from child subagents to parents (docs/design/25-company-systems-knowledge.md §2, WP 9).
/// </summary>
public sealed class GmtService
{
    private static readonly string[] _ownerEscalationKeywords =
    [
        "grant tool",
        "tool_grant",
        "tool grant",
        "charter text",
        "system prompt override",
    ];

    private readonly IGmtStore _store;
    private readonly LessonReviewer _defaultReviewer;
    private readonly TimeProvider _timeProvider;

    public GmtService(
        IGmtStore store,
        LessonReviewer defaultReviewer = LessonReviewer.Architect,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _defaultReviewer = defaultReviewer;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Stages a proposed lesson candidate from a child subagent trace before the child finishes (doc 25 §2).
    /// Enforces the taint cap floor: a lesson from a tainted child is capped at <see cref="MemoryTier.UntrustedDerived"/> (T1).
    /// </summary>
    public async Task<GmtCandidate> StageCandidateAsync(
        string childAgentId,
        string parentAgentId,
        string traceRef,
        string topic,
        string lessonText,
        bool childTainted,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childAgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentAgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(traceRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(lessonText);

        // Floor (doc 25 line 108): A GMT candidate from a tainted child is T1 (UntrustedDerived)
        var trustTier = childTainted
            ? MemoryTier.UntrustedDerived
            : MemoryTier.AgentVerified;

        var now = _timeProvider.GetUtcNow();
        var candidate = new GmtCandidate(
            CandidateId: Guid.NewGuid(),
            ChildAgentId: childAgentId,
            ParentAgentId: parentAgentId,
            TraceRef: traceRef,
            Topic: topic,
            LessonText: lessonText,
            TrustTier: trustTier,
            ChildTainted: childTainted,
            Status: GmtCandidateStatus.Staged, // Invisible to parent retrieval until accepted
            CreatedAt: now);

        await _store.StageCandidateAsync(candidate, ct).ConfigureAwait(false);
        return candidate;
    }

    /// <summary>
    /// Evaluates which reviewer role is required.
    /// Floor (doc 25 line 86): Tool grants and charter text always go to the owner; gmt.lesson_reviewer cannot route them elsewhere.
    /// </summary>
    public LessonReviewer DetermineRequiredReviewer(GmtCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        foreach (var keyword in _ownerEscalationKeywords)
        {
            if (candidate.LessonText.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return LessonReviewer.Owner;
            }
        }

        return _defaultReviewer;
    }

    /// <summary>Reviews a staged lesson candidate in the queue.</summary>
    public async Task<GmtReviewResult> ReviewCandidateAsync(
        Guid candidateId,
        GmtCandidateStatus decision,
        string reviewer,
        string? editedText = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewer);

        if (decision == GmtCandidateStatus.Staged)
        {
            return new GmtReviewResult(false, "Cannot review a candidate back to Staged status.");
        }

        if (decision == GmtCandidateStatus.Edited && string.IsNullOrWhiteSpace(editedText))
        {
            return new GmtReviewResult(false, "Edited text must be provided when decision is Edited.");
        }

        var candidate = await _store.FindAsync(candidateId, ct).ConfigureAwait(false);
        if (candidate == null)
        {
            return new GmtReviewResult(false, $"Candidate '{candidateId}' not found.");
        }

        if (candidate.Status != GmtCandidateStatus.Staged)
        {
            return new GmtReviewResult(false, $"Candidate '{candidateId}' is already in status '{candidate.Status}'.");
        }

        var now = _timeProvider.GetUtcNow();
        var success = await _store.UpdateReviewAsync(
            candidateId,
            decision,
            reviewer,
            now,
            editedText,
            ct).ConfigureAwait(false);

        if (!success)
        {
            return new GmtReviewResult(false, "Failed to update review status in store.");
        }

        var updated = candidate with
        {
            Status = decision,
            ReviewedBy = reviewer,
            ReviewedAt = now,
            EditedLessonText = editedText,
        };

        return new GmtReviewResult(true, null, updated);
    }

    /// <summary>Lists all candidates currently staged in the review queue.</summary>
    public Task<IReadOnlyList<GmtCandidate>> ListStagedAsync(string? parentAgentId = null, CancellationToken ct = default)
    {
        return _store.ListStagedAsync(parentAgentId, ct);
    }

    /// <summary>
    /// Retrieves active (accepted or edited) lessons for a parent agent.
    /// Crucial invariant (doc 25 §2, §3): Staged candidates are strictly invisible and excluded from retrieval.
    /// </summary>
    public Task<IReadOnlyList<GmtCandidate>> ListActiveLessonsAsync(string parentAgentId, CancellationToken ct = default)
    {
        return _store.ListActiveLessonsAsync(parentAgentId, ct);
    }
}
