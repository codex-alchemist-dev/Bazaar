namespace Bazaar.Server.Knowledge.Talent;

/// <summary>
/// Persistence interface for talent templates (<c>talent_templates</c> in doc 25 §2).
/// </summary>
public interface ITalentPoolStore
{
    /// <summary>Saves a talent template record.</summary>
    Task SaveAsync(TalentTemplate item, CancellationToken ct = default);

    /// <summary>Finds a template by ID and optional version (latest if null).</summary>
    Task<TalentTemplate?> FindAsync(string templateId, int? version = null, CancellationToken ct = default);

    /// <summary>Lists templates matching the optional status filter.</summary>
    Task<IReadOnlyList<TalentTemplate>> ListAsync(TalentTemplateStatus? status = null, CancellationToken ct = default);

    /// <summary>Gets the next version number for a template ID (starting at 1).</summary>
    Task<int> GetNextVersionAsync(string templateId, CancellationToken ct = default);

    /// <summary>Updates template status.</summary>
    Task<bool> UpdateStatusAsync(
        string templateId,
        int version,
        TalentTemplateStatus newStatus,
        string? approvedBy = null,
        DateTimeOffset? activatedAt = null,
        CancellationToken ct = default);
}
