namespace Bazaar.Server.Portability;

/// <summary>Who is asking for a personal-field export (doc 44 section 4.1: agents export nothing).</summary>
public enum PersonalExportRole
{
    /// <summary>The owner. May export every subject, and plaintext only through an owner session.</summary>
    Owner,

    /// <summary>Another human member. May export only their own subject, ciphertext only.</summary>
    Member,

    /// <summary>An agent. Always refused.</summary>
    Agent,
}

/// <summary>
/// The scope of one personal-field export (doc 44 sections 4.6, 10 and 12). Plaintext is opt-in twice: the scope must carry
/// <see cref="IsOwnerSession"/> and <see cref="IncludePlaintext"/>, and the setting
/// <c>portability.export.allow_plaintext_personal</c> must be true. Anything else gets ciphertext only.
/// </summary>
/// <param name="Actor">Audit actor id. Never a secret.</param>
/// <param name="Role">Requester role.</param>
/// <param name="IsOwnerSession">True only when the host authenticated this request as the owner's interactive session.</param>
/// <param name="IncludePlaintext">Explicit request for <c>personal/&lt;subject&gt;.json</c>.</param>
/// <param name="RequesterSubjectId">The member's own subject id. Required for <see cref="PersonalExportRole.Member"/>.</param>
/// <param name="SubjectIds">Optional subject filter. Null means every subject the role may export.</param>
public sealed record PersonalExportScope(
    string Actor,
    PersonalExportRole Role,
    bool IsOwnerSession = false,
    bool IncludePlaintext = false,
    string? RequesterSubjectId = null,
    IReadOnlySet<string>? SubjectIds = null);

/// <summary>Why a personal-field export was refused. Reasons carry no personal data.</summary>
public enum PersonalExportRefusal
{
    AgentNotAllowed,
    PlaintextNeedsOwnerSession,
    PlaintextSettingOff,
    SubjectOutOfScope,
    InvalidSubjectId,
    InvalidEnvelope,
    PlaintextUnavailable,
    AuditUnavailable,
}

/// <summary>Thrown when an export is refused. The message never contains personal data or key material.</summary>
public sealed class PersonalExportRefusedException(PersonalExportRefusal reason)
    : Exception("Personal-field export refused: " + reason)
{
    public PersonalExportRefusal Reason { get; } = reason;
}
