namespace Bazaar.Server.Knowledge.Talent;

/// <summary>Result of submitting a template manifest.</summary>
public sealed record TemplateSubmitResult(
    bool Succeeded,
    string? Error = null,
    TalentTemplate? Template = null);

/// <summary>Result of applying a Council hire motion to a talent template.</summary>
public sealed record TemplateHireResult(
    bool Succeeded,
    string? Error = null,
    TalentTemplate? Template = null);

/// <summary>
/// Service interface for the Talent Pool (docs/design/25-company-systems-knowledge.md §2, §3, §4).
/// </summary>
public interface ITalentPool
{
    /// <summary>
    /// Submits a Soul template to the Talent Pool. Validates the manifest and stores it in Quarantined status.
    /// </summary>
    Task<TemplateSubmitResult> SubmitAsync(
        TalentManifest manifest,
        string actor,
        Guid? sourceSoulId = null,
        CancellationToken ct = default);

    /// <summary>Gets a template by ID and optional version.</summary>
    Task<TalentTemplate?> GetTemplateAsync(string templateId, int? version = null, CancellationToken ct = default);

    /// <summary>Lists templates, optionally filtered by status.</summary>
    Task<IReadOnlyList<TalentTemplate>> ListTemplatesAsync(TalentTemplateStatus? status = null, CancellationToken ct = default);

    /// <summary>Computes a textual diff between two versions of a template.</summary>
    Task<string> GetDiffAsync(string templateId, int versionA, int versionB, CancellationToken ct = default);

    /// <summary>
    /// Applies a Council hire motion approval (doc 10, C32), transitioning the template from Quarantined to Active.
    /// </summary>
    Task<TemplateHireResult> ApplyHireMotionAsync(
        string templateId,
        int version,
        string approvedBy,
        CancellationToken ct = default);
}
