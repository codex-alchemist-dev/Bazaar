namespace Bazaar.Server.CompanyKnowledge.Abstractions;

/// <summary>What an evidence entry says about the note.</summary>
public enum CompanyEvidenceRole
{
    /// <summary>The source supports the note.</summary>
    Confirms,

    /// <summary>The source contradicts the note.</summary>
    Contradicts,
}

/// <summary>State of an evidence source (doc 08, 6.7).</summary>
public enum CompanyEvidenceStatus
{
    /// <summary>The source exists and counts.</summary>
    Live,

    /// <summary>The source was deprecated.</summary>
    DeprecatedSource,

    /// <summary>The source was erased; only the role remains.</summary>
    Tombstone,
}

/// <summary>One provenance entry of <c>company.evidence</c>. Provenance only: never an edge for retrieval.</summary>
/// <param name="DocId">Source note id; null only for a tombstone whose id was erased.</param>
/// <param name="Project">Source project slug.</param>
/// <param name="Role">Confirms or contradicts.</param>
/// <param name="Author">Author, such as <c>agent:charles</c>.</param>
/// <param name="At">When the source was written (ISO 8601 text, kept as written).</param>
/// <param name="OriginTrust">The source's tier, such as <c>T2</c>.</param>
/// <param name="Independent">False for an echo (the writer had retrieved this company note).</param>
/// <param name="Status">Live, deprecated-source or tombstone.</param>
public sealed record CompanyEvidence(
    string? DocId,
    string? Project,
    CompanyEvidenceRole Role,
    string? Author,
    string? At,
    string? OriginTrust,
    bool Independent,
    CompanyEvidenceStatus Status);

/// <summary>A proposal for the company tier, from an agent, the owner or the distillation job.</summary>
/// <param name="Proposer"><c>agent:&lt;id&gt;</c> or <c>owner</c>.</param>
/// <param name="Kind">The company kind.</param>
/// <param name="Title">Note title.</param>
/// <param name="Statement">One generalised sentence.</param>
/// <param name="AppliesWhen">At least one condition.</param>
/// <param name="NotWhen">Optional exclusions.</param>
/// <param name="Evidence">Provenance; at least one confirming entry unless the owner proposes.</param>
/// <param name="Tier">1 when any input was tainted or untrusted-derived, 2 when every input is clean T2 or higher, 3 for an owner write.</param>
/// <param name="Body">Markdown body; wiki-links may name notes under <c>company/</c> only.</param>
/// <param name="DerivedFrom">Company note ids this note was built from.</param>
/// <param name="Supersedes">Company note ids this note replaces.</param>
/// <param name="ConditionedOn">Relation targets (doc 64, 5.3).</param>
/// <param name="Affects">Relation targets.</param>
/// <param name="ResolvedBy">Relation targets: a fix, decision or charter, never a person.</param>
/// <param name="DistilledByRun">Distillation run id, or null for a direct proposal.</param>
/// <param name="DistilledByReaderSession">Reader session id, or null.</param>
public sealed record CompanyProposal(
    string Proposer,
    CompanyKind Kind,
    string Title,
    string Statement,
    IReadOnlyList<string> AppliesWhen,
    IReadOnlyList<string> NotWhen,
    IReadOnlyList<CompanyEvidence> Evidence,
    int Tier,
    string Body,
    IReadOnlyList<string> DerivedFrom,
    IReadOnlyList<string> Supersedes,
    IReadOnlyList<string> ConditionedOn,
    IReadOnlyList<string> Affects,
    IReadOnlyList<string> ResolvedBy,
    string? DistilledByRun = null,
    string? DistilledByReaderSession = null);

/// <summary>Who admits a candidate (doc 08, 6.4). Ordered by strength: a stronger authority may do what a weaker one may.</summary>
public enum CompanyAdmissionAuthority
{
    /// <summary>The review window ran out with no objection (default for T2).</summary>
    ReviewWindow,

    /// <summary>A Council motion decided.</summary>
    Council,

    /// <summary>The owner.</summary>
    Owner,
}

/// <summary>An admission request.</summary>
/// <param name="Authority">The authority claimed.</param>
/// <param name="ApprovedBy">Owner identity (for <see cref="CompanyAdmissionAuthority.Owner"/>) or the motion id (for Council); ignored for the window.</param>
/// <param name="Objected">True when an objection is open against the candidate; blocks the window path.</param>
public sealed record CompanyAdmissionRequest(CompanyAdmissionAuthority Authority, string? ApprovedBy, bool Objected = false);

/// <summary>Why a staging or admission step refused. Codes only; never note text.</summary>
public enum CompanyRefusal
{
    /// <summary>The proposer is quarantined.</summary>
    ProposerQuarantined,

    /// <summary>The proposer is over the weekly proposal quota.</summary>
    QuotaExceeded,

    /// <summary>The note does not pass validation; see the findings.</summary>
    Invalid,

    /// <summary>A referenced company note does not resolve to a note under <c>company/</c>.</summary>
    UnresolvedReference,

    /// <summary>The candidate is not found or not readable.</summary>
    NotFound,

    /// <summary>The candidate was already admitted or rejected.</summary>
    NotACandidate,

    /// <summary>The candidate is T1; nobody may admit it (doc 08, 6.4).</summary>
    TierTooLow,

    /// <summary>The claimed authority is weaker than the install requires or than the tier allows.</summary>
    AuthorityInsufficient,

    /// <summary>The review window has not ended, is disabled, or an objection is open.</summary>
    ReviewWindowOpen,

    /// <summary>A note already exists at the target path.</summary>
    Duplicate,

    /// <summary>The Vault commit failed.</summary>
    CommitFailed,
}

/// <summary>Result of staging, admitting or rejecting.</summary>
/// <param name="Succeeded">True on success.</param>
/// <param name="Path">The path written (candidate or admitted note), or the candidate on refusal.</param>
/// <param name="Refusal">The refusal, or null.</param>
/// <param name="Findings">Validation finding codes, when the refusal is <see cref="CompanyRefusal.Invalid"/>.</param>
/// <param name="Detail">A short structural detail (a commit failure reason), never note text.</param>
public sealed record CompanyOutcome(
    bool Succeeded,
    string? Path,
    CompanyRefusal? Refusal,
    IReadOnlyList<string> Findings,
    string? Detail)
{
    /// <summary>A success.</summary>
    public static CompanyOutcome Ok(string path) => new(true, path, null, [], null);

    /// <summary>A refusal.</summary>
    public static CompanyOutcome Refused(CompanyRefusal refusal, string? path = null, IReadOnlyList<string>? findings = null, string? detail = null) =>
        new(false, path, refusal, findings ?? [], detail);
}

/// <summary>What the reader session says a candidate does to an existing note (doc 08, 6.3 step 3).</summary>
public enum CompanyRelationToExisting
{
    /// <summary>Independent of existing notes.</summary>
    None,

    /// <summary>Agrees: the sources are appended to the existing note's evidence.</summary>
    Agrees,

    /// <summary>Refines: the candidate would supersede the existing note.</summary>
    Refines,

    /// <summary>Contradicts: both are kept and contested.</summary>
    Contradicts,
}

/// <summary>One source note handed to the distiller. Project areas only (doc 08, 6.3 step 1).</summary>
/// <param name="DocId">Source note id.</param>
/// <param name="Project">Project slug.</param>
/// <param name="Author">Author.</param>
/// <param name="At">When written.</param>
/// <param name="OriginTrust">Tier, such as <c>T2</c>.</param>
/// <param name="Title">Title.</param>
/// <param name="Body">Body.</param>
public sealed record CompanySourceNote(string DocId, string Project, string Author, DateTimeOffset At, string OriginTrust, string Title, string Body);

/// <summary>One group of sources distilled into one candidate lesson.</summary>
/// <param name="RunId">Run id (a ULID).</param>
/// <param name="Sources">The group's inputs; an evidence entry naming any other id is dropped.</param>
public sealed record CompanyDistillRequest(string RunId, IReadOnlyList<CompanySourceNote> Sources);

/// <summary>Structured distiller output (doc 08, 6.3 step 3). The deterministic pipeline sanitizes and stages it.</summary>
/// <param name="Kind">Company kind.</param>
/// <param name="Title">Title.</param>
/// <param name="Statement">Statement.</param>
/// <param name="AppliesWhen">Conditions.</param>
/// <param name="NotWhen">Exclusions.</param>
/// <param name="EvidenceDocIds">Source ids and roles; each must be an input of the group.</param>
/// <param name="Relation">Relation to an existing note.</param>
/// <param name="RelatedNoteId">The existing note's id when <paramref name="Relation"/> is not None.</param>
/// <param name="Body">Body.</param>
public sealed record CompanyCandidateDraft(
    CompanyKind Kind,
    string Title,
    string Statement,
    IReadOnlyList<string> AppliesWhen,
    IReadOnlyList<string> NotWhen,
    IReadOnlyList<(string DocId, CompanyEvidenceRole Role)> EvidenceDocIds,
    CompanyRelationToExisting Relation,
    string? RelatedNoteId,
    string Body);

/// <summary>
/// Seam for the distillation step that calls a model in a reader session (doc 08, 6.3 step 3). No implementation ships here: it
/// needs reader sessions and the gateway. A host that has one registers it; the staging path never depends on it.
/// </summary>
public interface ICompanyDistiller
{
    /// <summary>Distils one group of sources into zero or more candidate drafts.</summary>
    Task<IReadOnlyList<CompanyCandidateDraft>> DistilAsync(CompanyDistillRequest request, CancellationToken ct);
}

/// <summary>
/// Audit seam for the <c>company_knowledge.*</c> events of doc 08, 6.3 (<c>candidate.staged</c>, <c>admitted</c>, <c>rejected</c>).
/// Ids and codes only, never titles, statements or bodies.
/// </summary>
public interface ICompanyKnowledgeAudit
{
    /// <summary>Records one state change.</summary>
    Task RecordAsync(string eventName, string noteId, string? actor, IReadOnlyDictionary<string, string> data, CancellationToken ct);
}
