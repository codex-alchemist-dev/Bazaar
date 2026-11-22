namespace Bazaar.Server.GeneticMemory.Abstractions;

/// <summary>Names a stage call must keep out of a candidate. Matching is whole-word and ignores case.</summary>
/// <param name="People">Person names or handles.</param>
/// <param name="Customers">Customer names.</param>
/// <param name="Projects">Project names or slugs.</param>
public sealed record GmtTerms(
    IReadOnlyList<string> People,
    IReadOnlyList<string> Customers,
    IReadOnlyList<string> Projects)
{
    /// <summary>No terms.</summary>
    public static GmtTerms None { get; } = new([], [], []);
}

/// <summary>A child subagent's proposal, written before the child ends.</summary>
/// <param name="ChildAgent">Agent slug of the subagent (provenance).</param>
/// <param name="TargetAgent">Agent slug of the receiver.</param>
/// <param name="ContentClass">What the proposal is.</param>
/// <param name="Kind">Lesson kind.</param>
/// <param name="Title">Title.</param>
/// <param name="Body">Body text. Written by a reader session with no tools (doc 25).</param>
/// <param name="TraceRef">Link to the child's trace.</param>
/// <param name="ChildTainted">True when the child saw untrusted input.</param>
/// <param name="ChildQuarantined">True when the child is quarantined.</param>
/// <param name="Terms">Names to keep out.</param>
public sealed record GmtStageRequest(
    string ChildAgent,
    string TargetAgent,
    GmtContentClass ContentClass,
    GmtLessonKind Kind,
    string Title,
    string Body,
    string? TraceRef,
    bool ChildTainted,
    bool ChildQuarantined,
    GmtTerms Terms);

/// <summary>
/// Genetic Memory Transfer (docs/design/25-company-systems-knowledge.md, doc 07): a subagent stages candidate
/// lessons, a reviewer accepts, edits or rejects them, and only an accepted lesson becomes a memory note.
/// Nothing transfers automatically, and nothing from private memory, preferences, GC text or a Soul export is accepted.
/// </summary>
public interface IGeneticMemoryTransfer
{
    /// <summary>Stages a candidate. Staged text is held outside the Vault, so retrieval cannot see it.</summary>
    Task<GmtOutcome> StageAsync(GmtStageRequest request, CancellationToken ct);

    /// <summary>The queue for a reviewer role: staged candidates that role may decide.</summary>
    Task<IReadOnlyList<GmtCandidate>> QueueAsync(GmtReviewer role, CancellationToken ct);

    /// <summary>Accepts a candidate, optionally with an edited title and body that are screened again.</summary>
    Task<GmtOutcome> AcceptAsync(Guid id, GmtReviewer role, string? editedTitle, string? editedBody, CancellationToken ct);

    /// <summary>Rejects a candidate.</summary>
    Task<GmtOutcome> RejectAsync(Guid id, GmtReviewer role, CancellationToken ct);
}

/// <summary>Seam for the platform's secret scanner, so this module holds no reference to the server project.</summary>
public interface IGmtSecretScanner
{
    /// <summary>True when the text holds a credential-shaped token.</summary>
    bool ContainsSecret(string text);
}

/// <summary>An accepted lesson to write as a memory note in the receiving agent's private area.</summary>
/// <param name="CandidateId">Candidate id; makes the note id stable.</param>
/// <param name="Path">Vault path, <c>agents/&lt;target&gt;/memory/lessons|failures/&lt;slug&gt;.md</c>.</param>
/// <param name="Kind">Lesson kind.</param>
/// <param name="Title">Title.</param>
/// <param name="Body">Body text.</param>
/// <param name="ChildAgent">Provenance: the child that proposed it.</param>
/// <param name="TraceRef">Link to the child's trace, or null.</param>
/// <param name="Tainted">True when the child was tainted; the note is then T1, otherwise T2. Never higher.</param>
public sealed record GmtLessonWrite(
    Guid CandidateId, string Path, GmtLessonKind Kind, string Title, string Body, string ChildAgent, string? TraceRef, bool Tainted);

/// <summary>Result of writing a lesson.</summary>
public enum GmtWriteStatus
{
    /// <summary>Written.</summary>
    Written,

    /// <summary>The path is outside the allowed memory areas.</summary>
    PathInvalid,

    /// <summary>The note could not be built.</summary>
    NoteInvalid,

    /// <summary>The memory commit failed.</summary>
    CommitFailed,
}

/// <summary>Outcome of <see cref="IGmtLessonWriter.WriteAsync"/>.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Detail">The failure reason, or null.</param>
public sealed record GmtWriteResult(GmtWriteStatus Status, string? Detail = null);

/// <summary>Writes an accepted lesson through the memory write path (notes, trust tier, one commit).</summary>
public interface IGmtLessonWriter
{
    /// <summary>Writes one note as <paramref name="writerAgentId"/>.</summary>
    Task<GmtWriteResult> WriteAsync(GmtLessonWrite write, string writerAgentId, CancellationToken ct);
}

/// <summary>One lesson a child proposes.</summary>
/// <param name="ContentClass">What it is.</param>
/// <param name="Kind">Lesson kind.</param>
/// <param name="Title">Title.</param>
/// <param name="Body">Body text.</param>
public sealed record GmtProposal(GmtContentClass ContentClass, GmtLessonKind Kind, string Title, string Body);

/// <summary>What the host knows about a child that ended: who it was, who receives its lessons and what it proposed.</summary>
/// <param name="ChildAgent">Provenance of the child.</param>
/// <param name="TargetAgent">The receiving agent slug (the parent).</param>
/// <param name="TraceRef">Link to the child's trace.</param>
/// <param name="ChildTainted">True when the child saw untrusted input.</param>
/// <param name="ChildQuarantined">True when the child is quarantined.</param>
/// <param name="Terms">Names to keep out.</param>
/// <param name="Proposals">The proposed lessons, written by a reader session with no tools (doc 25).</param>
public sealed record GmtChildProposals(
    string ChildAgent,
    string TargetAgent,
    string? TraceRef,
    bool ChildTainted,
    bool ChildQuarantined,
    GmtTerms Terms,
    IReadOnlyList<GmtProposal> Proposals);

/// <summary>
/// Reads the proposals of a child turn that ended. The host provides it (it knows the trace, the session's taint and the agent
/// slugs). With none registered, no hook is installed and nothing is staged.
/// </summary>
public interface IGmtProposalSource
{
    /// <summary>The proposals for an ended child, or null when it proposed none.</summary>
    Task<GmtChildProposals?> ReadAsync(Bazaar.Server.Agents.Turns.Subagents.SubagentEnded ended, CancellationToken ct);
}
