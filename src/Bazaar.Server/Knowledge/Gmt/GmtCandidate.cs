using Bazaar.Server.Memory.Trust;

namespace Bazaar.Server.Knowledge.Gmt;

/// <summary>Status of a Genetic Memory Transfer candidate lesson (docs/design/25-company-systems-knowledge.md §2, §3).</summary>
public enum GmtCandidateStatus
{
    /// <summary>Staged in the staging area; strictly invisible to parent retrieval until accepted (doc 25 §2, §3).</summary>
    Staged,

    /// <summary>Accepted into persistent memory without changes.</summary>
    Accepted,

    /// <summary>Accepted into persistent memory with reviewer edits.</summary>
    Edited,

    /// <summary>Rejected by the reviewer and discarded.</summary>
    Rejected,
}

/// <summary>
/// A candidate lesson proposed by a finishing child subagent for transfer to its parent (doc 25 §2, WP 9).
/// </summary>
/// <param name="CandidateId">Unique identifier of the staged lesson candidate.</param>
/// <param name="ChildAgentId">Child subagent that produced the lesson.</param>
/// <param name="ParentAgentId">Parent agent receiving the lesson.</param>
/// <param name="TraceRef">Reference to the child subagent execution trace.</param>
/// <param name="Topic">Lesson topic or category.</param>
/// <param name="LessonText">The candidate lesson text proposed by the subagent.</param>
/// <param name="TrustTier">Assigned trust tier. Strictly capped at <see cref="MemoryTier.UntrustedDerived"/> if child was tainted.</param>
/// <param name="ChildTainted">Whether the child subagent session was tainted.</param>
/// <param name="Status">Review lifecycle status.</param>
/// <param name="CreatedAt">When the candidate was staged.</param>
/// <param name="ReviewedAt">When the reviewer acted on the candidate.</param>
/// <param name="ReviewedBy">Who reviewed the candidate.</param>
/// <param name="EditedLessonText">Edited text if approved with edits.</param>
public sealed record GmtCandidate(
    Guid CandidateId,
    string ChildAgentId,
    string ParentAgentId,
    string TraceRef,
    string Topic,
    string LessonText,
    MemoryTier TrustTier,
    bool ChildTainted,
    GmtCandidateStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReviewedAt = null,
    string? ReviewedBy = null,
    string? EditedLessonText = null);
