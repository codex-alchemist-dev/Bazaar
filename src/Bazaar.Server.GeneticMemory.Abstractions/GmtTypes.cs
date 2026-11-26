namespace Bazaar.Server.GeneticMemory.Abstractions;

/// <summary>What a child agent proposes to hand to another agent (docs/design/25-company-systems-knowledge.md, Genetic Memory Transfer).</summary>
public enum GmtContentClass
{
    /// <summary>A procedural lesson. The only class that becomes a memory note.</summary>
    Lesson,

    /// <summary>A tool grant. Always reviewed by the owner and never written to memory here (the approval ladder applies it).</summary>
    ToolGrant,

    /// <summary>Charter text. Always reviewed by the owner and never written to memory here.</summary>
    CharterText,

    /// <summary>A preference of an agent or the owner. Never transfers.</summary>
    Preference,

    /// <summary>Private memory that is not a lesson. Never transfers.</summary>
    PrivateMemory,

    /// <summary>GC message text. Never transfers.</summary>
    GcText,

    /// <summary>Reputation data. Never transfers.</summary>
    Reputation,

    /// <summary>Any part of a Soul export. Soul export never carries memory, so it never transfers.</summary>
    SoulExport,
}

/// <summary>The kind of lesson a candidate is.</summary>
public enum GmtLessonKind
{
    /// <summary>A procedural lesson, written under <c>lessons/</c>.</summary>
    Procedure,

    /// <summary>A failure lesson, written under <c>failures/</c>.</summary>
    Failure,
}

/// <summary>Who reviews a candidate (doc 25 setting <c>gmt.lesson_reviewer</c>).</summary>
public enum GmtReviewer
{
    /// <summary>The architect or the owner.</summary>
    Architect,

    /// <summary>The owner only.</summary>
    Owner,
}

/// <summary>Where a candidate is in the review queue.</summary>
public enum GmtStatus
{
    /// <summary>Staged and invisible to retrieval.</summary>
    Staged,

    /// <summary>Accepted and written as a memory note.</summary>
    Accepted,

    /// <summary>Rejected. The text is kept for audit and never retrievable.</summary>
    Rejected,
}

/// <summary>Why a stage, accept or reject call was refused. Codes only; matched text is never returned.</summary>
public enum GmtRefusal
{
    /// <summary>A required field is empty or malformed.</summary>
    InvalidInput,

    /// <summary>The content class never transfers.</summary>
    NotTransferable,

    /// <summary>The child and the target are the same agent.</summary>
    SelfTransfer,

    /// <summary>The child is quarantined, so nothing it proposes is staged.</summary>
    ChildQuarantined,

    /// <summary>The text holds a credential-shaped token.</summary>
    SecretFound,

    /// <summary>The text names a person, customer or project that was passed as a term to keep out.</summary>
    PersonalData,

    /// <summary>The same child already staged the same title for the same target.</summary>
    Duplicate,

    /// <summary>No such candidate.</summary>
    NotFound,

    /// <summary>The candidate is already accepted or rejected.</summary>
    AlreadyDecided,

    /// <summary>The reviewer role may not decide this candidate.</summary>
    ReviewerNotAllowed,

    /// <summary>The memory path is not allowed.</summary>
    PathInvalid,

    /// <summary>The memory write failed. The candidate stays staged.</summary>
    CommitFailed,
}

/// <summary>Outcome of a stage, accept, edit or reject call.</summary>
/// <param name="Succeeded">True when the call did what was asked.</param>
/// <param name="Id">The candidate id, when known.</param>
/// <param name="Path">The memory note written on accept, or null.</param>
/// <param name="Refusal">Why it was refused, or null.</param>
/// <param name="Detail">Finding codes or a failure reason, or null.</param>
public sealed record GmtOutcome(bool Succeeded, Guid? Id, string? Path, GmtRefusal? Refusal, string? Detail)
{
    /// <summary>A staged or decided candidate.</summary>
    public static GmtOutcome Ok(Guid id, string? path = null) => new(true, id, path, null, null);

    /// <summary>A refused call.</summary>
    public static GmtOutcome Refused(GmtRefusal refusal, string? detail = null, Guid? id = null) => new(false, id, null, refusal, detail);
}

/// <summary>A staged candidate as the review queue shows it.</summary>
/// <param name="Id">Candidate id.</param>
/// <param name="ChildAgent">The subagent that proposed it (provenance).</param>
/// <param name="TargetAgent">The agent that would receive it.</param>
/// <param name="ContentClass">What it is.</param>
/// <param name="Kind">Lesson kind; meaningful for lessons.</param>
/// <param name="Title">Title.</param>
/// <param name="Body">Body text.</param>
/// <param name="TraceRef">Link to the child's trace, or null.</param>
/// <param name="Tainted">True when the child had seen untrusted input; the note stays at T1.</param>
/// <param name="RequiredReviewer">Who may decide it.</param>
/// <param name="Status">Queue status.</param>
/// <param name="StagedAt">When it was staged.</param>
/// <param name="AcceptedPath">The memory note path once accepted, otherwise null.</param>
public sealed record GmtCandidate(
    Guid Id,
    string ChildAgent,
    string TargetAgent,
    GmtContentClass ContentClass,
    GmtLessonKind Kind,
    string Title,
    string Body,
    string? TraceRef,
    bool Tainted,
    GmtReviewer RequiredReviewer,
    GmtStatus Status,
    DateTimeOffset StagedAt,
    string? AcceptedPath = null);
