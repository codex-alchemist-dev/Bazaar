namespace Bazaar.Server.TalentPool;

/// <summary>
/// A candidate Soul template (docs/design/25-company-systems-knowledge.md, Talent Pool). Manifest only: prompt, tool
/// allowlist and model whitelist. It carries no identity, lessons or memory (docs/design/07-soul-system.md, Creation).
/// </summary>
/// <param name="Name">The template name. It is the stable key across versions.</param>
/// <param name="Prompt">The Soul prompt. Executable intent, so it is quarantined until a Council hire vote passes.</param>
/// <param name="ToolAllowlist">Tools the template asks for. Any entry makes instantiation a Tier 2 grant.</param>
/// <param name="ModelWhitelist">Models the template may run on. Each must be on the install whitelist.</param>
/// <param name="Files">Paths of any extra files submitted with the manifest. A memory file is rejected.</param>
/// <param name="SourceSoulId">The Soul that offered the template, or null for an imported one.</param>
public sealed record SoulManifest(
    string Name,
    string Prompt,
    IReadOnlyList<string> ToolAllowlist,
    IReadOnlyList<string> ModelWhitelist,
    IReadOnlyList<string> Files,
    string? SourceSoulId = null);

/// <summary>One stored version of a Talent template.</summary>
/// <param name="TemplateId">The stable template id, derived from the name.</param>
/// <param name="Version">The version, starting at 1. A changed manifest is a new version.</param>
/// <param name="Manifest">The validated manifest.</param>
/// <param name="ContentSha">Lowercase hex SHA-256 of the canonical manifest.</param>
/// <param name="SubmittedBy">The actor that submitted it.</param>
/// <param name="SubmittedAt">When it was submitted.</param>
public sealed record TalentTemplate(
    string TemplateId,
    int Version,
    SoulManifest Manifest,
    string ContentSha,
    string SubmittedBy,
    DateTimeOffset SubmittedAt)
{
    /// <summary>The key the Council hire vote uses. Version-specific, so a new version needs its own vote.</summary>
    public string HireKey => $"{TemplateId}@{Version}";
}

/// <summary>The outcome of a submission.</summary>
/// <param name="TemplateId">The template id.</param>
/// <param name="Version">The version stored or matched.</param>
/// <param name="ContentSha">The content hash.</param>
/// <param name="IsDuplicate">True when an identical version already existed, so nothing new was stored.</param>
public sealed record TemplateSubmitResult(string TemplateId, int Version, string ContentSha, bool IsDuplicate);

/// <summary>Where a template stands before hire.</summary>
public enum TemplateStanding
{
    /// <summary>Quarantined: no hire vote has been opened.</summary>
    Quarantined,

    /// <summary>A Council hire vote is open.</summary>
    VotePending,

    /// <summary>The hire vote passed. The template may be instantiated.</summary>
    Released,

    /// <summary>The latest hire vote was rejected or withdrawn. It may be proposed again.</summary>
    Blocked,
}

/// <summary>A template with its hire standing.</summary>
public sealed record TalentTemplateView(TalentTemplate Template, TemplateStanding Standing);

/// <summary>A submitted manifest broke a rule. Nothing was stored.</summary>
public sealed class ManifestRejectedException(string reason) : Exception(reason);

/// <summary>Instantiation was refused: the template is not released, a model is off the whitelist, or a tool grant lacks Tier 2 approval.</summary>
public sealed class InstantiationRefusedException(string reason) : Exception(reason);
