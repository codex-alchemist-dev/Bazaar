namespace Bazaar.Server.Knowledge.Talent;

/// <summary>Lifecycle status of a Talent Pool template (doc 25 §2, §3).</summary>
public enum TalentTemplateStatus
{
    /// <summary>Newly submitted template, quarantined pending Council hire motion approval (C32).</summary>
    Quarantined,

    /// <summary>Approved by Council hire motion and available for instantiation.</summary>
    Active,

    /// <summary>Replaced by a newer version or explicitly deprecated.</summary>
    Deprecated,

    /// <summary>Explicitly retired.</summary>
    Retired,
}

/// <summary>
/// A versioned talent template in the Talent Pool (doc 25 §2).
/// Backed by the <c>talent_templates</c> table.
/// </summary>
/// <param name="TemplateId">Stable slug identifying the template.</param>
/// <param name="Version">1-based version number.</param>
/// <param name="Manifest">The validated behavioural manifest.</param>
/// <param name="ContentSha">SHA-256 hex string of canonical manifest representation.</param>
/// <param name="Status">Lifecycle status.</param>
/// <param name="SourceSoulId">Optional source Soul ID if offered by an existing agent.</param>
/// <param name="SubmittedBy">Identity of the submitter.</param>
/// <param name="CreatedAt">When the version was submitted.</param>
/// <param name="ActivatedAt">When the version was approved by Council hire motion.</param>
/// <param name="ApprovedBy">Who approved or recorded the Council hire motion.</param>
public sealed record TalentTemplate(
    string TemplateId,
    int Version,
    TalentManifest Manifest,
    string ContentSha,
    TalentTemplateStatus Status,
    Guid? SourceSoulId,
    string SubmittedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ActivatedAt = null,
    string? ApprovedBy = null);
