namespace Bazaar.Server.TalentPool;

/// <summary>
/// A Soul template as offered to the Talent Pool (docs/design/25, Talent Pool row): prompt, tool allowlist and model whitelist only.
/// There is no memory in a template. <see cref="Files"/> and <see cref="ExtraFields"/> exist so a payload that carries anything else
/// can be rejected by name instead of being dropped silently.
/// </summary>
/// <param name="Prompt">The Soul's system prompt (charter text).</param>
/// <param name="ToolAllowlist">Tool names the template may be granted. Listing a tool is not a grant.</param>
/// <param name="ModelWhitelist">Models the template may run on.</param>
/// <param name="TemplateId">Set when this submission is a new version of an existing template; null starts a new template.</param>
/// <param name="Files">Paths of any files bundled with the submission. A valid manifest has none.</param>
/// <param name="ExtraFields">Names of any other fields the submitted payload carried. A valid manifest has none.</param>
public sealed record TalentManifest(
    string Prompt,
    IReadOnlyList<string> ToolAllowlist,
    IReadOnlyList<string> ModelWhitelist,
    Guid? TemplateId = null,
    IReadOnlyList<string>? Files = null,
    IReadOnlyList<string>? ExtraFields = null);

/// <summary>A manifest that passed validation, in canonical form (sorted, de-duplicated lists; LF line endings) with its content hash.</summary>
/// <param name="Manifest">The canonical manifest. Its <c>Files</c> and <c>ExtraFields</c> are empty.</param>
/// <param name="ContentSha">Lowercase hex SHA-256 of the canonical JSON. Identical content gives the identical hash.</param>
public sealed record ValidatedTalentManifest(TalentManifest Manifest, string ContentSha);

/// <summary>One reason a manifest was refused. <see cref="Code"/> is stable; <see cref="Message"/> is for people.</summary>
public sealed record TalentManifestError(string Code, string Message);

/// <summary>Who is acting. The principal model is owned elsewhere (docs/design/15); this is the narrow slice the Talent Pool needs.</summary>
/// <param name="PrincipalId">Opaque principal id. Never blank.</param>
/// <param name="SoulId">The Soul on whose behalf a template is offered, when the actor is an agent. Recorded as <c>source_soul_id</c>.</param>
public sealed record TalentActor(string PrincipalId, Guid? SoulId = null);

/// <summary>Lifecycle of a stored template. Imports start quarantined; only a passed Council <c>hire</c> motion moves one to hired.</summary>
public enum TalentTemplateStatus
{
    Quarantined,
    Hired,
    Rejected,
}

/// <summary>One stored version of a template (a <c>talent_templates</c> row).</summary>
public sealed record TalentTemplate(
    Guid TemplateId,
    int Version,
    TalentManifest Manifest,
    string ContentSha,
    TalentTemplateStatus Status,
    Guid? SourceSoulId);

/// <summary>Result of adding a version to the store. <see cref="Created"/> is false when the same content already exists for the template.</summary>
public sealed record TalentTemplateAddResult(TalentTemplate Template, bool Created);

public enum TemplateSubmitOutcome
{
    /// <summary>Stored as a new version, status quarantined. It is not instantiable.</summary>
    Quarantined,

    /// <summary>The same content was already stored for this template; nothing new was written.</summary>
    Duplicate,

    /// <summary>Refused. See <see cref="TemplateSubmitResult.Errors"/>. Nothing was stored.</summary>
    Rejected,
}

public sealed record TemplateSubmitResult(
    TemplateSubmitOutcome Outcome,
    Guid? TemplateId,
    int? Version,
    string? ContentSha,
    IReadOnlyList<TalentManifestError> Errors)
{
    public static TemplateSubmitResult Refused(params TalentManifestError[] errors) =>
        new(TemplateSubmitOutcome.Rejected, null, null, null, errors);
}

/// <summary>The difference between two versions of one template.</summary>
public sealed record TemplateVersionDiff(
    Guid TemplateId,
    int FromVersion,
    int ToVersion,
    bool PromptChanged,
    IReadOnlyList<string> ToolsAdded,
    IReadOnlyList<string> ToolsRemoved,
    IReadOnlyList<string> ModelsAdded,
    IReadOnlyList<string> ModelsRemoved)
{
    public bool IsEmpty => !PromptChanged && ToolsAdded.Count == 0 && ToolsRemoved.Count == 0 && ModelsAdded.Count == 0 && ModelsRemoved.Count == 0;
}

/// <summary>A request to turn a hired template into a new Soul. Soul creation itself belongs to docs/design/07 and is not built here.</summary>
/// <param name="GrantedTools">Tools the new Soul would be given. Any entry makes the instantiation Tier 2 (docs/design/25, 07, 15).</param>
/// <param name="Models">Models the new Soul would run on; each must be on the template's whitelist and the install's whitelist.</param>
public sealed record TalentInstantiateRequest(
    Guid TemplateId,
    int Version,
    TalentActor Actor,
    IReadOnlyList<string> GrantedTools,
    IReadOnlyList<string> Models)
{
    /// <summary>True when any tool is granted: the instantiation needs Tier 2 approval.</summary>
    public bool RequiresTier2 => GrantedTools.Count > 0;
}

public enum InstantiationRefusal
{
    None,
    UnknownTemplate,
    NotHired,
    HireNotApproved,
    ToolNotAllowed,
    ModelNotAllowed,
}

/// <summary>Whether a template may be instantiated. <see cref="Allowed"/> permits the caller to go on to Soul creation; it creates nothing.</summary>
public sealed record TalentInstantiationDecision(bool Allowed, InstantiationRefusal Refusal, bool RequiresTier2, TalentTemplate? Template)
{
    public static TalentInstantiationDecision Refuse(InstantiationRefusal reason, bool tier2, TalentTemplate? template = null) =>
        new(false, reason, tier2, template);
}
