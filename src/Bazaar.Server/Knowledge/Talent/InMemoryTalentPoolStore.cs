using System.Collections.Concurrent;

namespace Bazaar.Server.Knowledge.Talent;

/// <summary>
/// In-memory implementation of <see cref="ITalentPoolStore"/> for fast headless tests and local execution.
/// </summary>
public sealed class InMemoryTalentPoolStore : ITalentPoolStore
{
    private readonly ConcurrentDictionary<(string TemplateId, int Version), TalentTemplate> _templates = new();

    public Task SaveAsync(TalentTemplate item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        _templates[(item.TemplateId, item.Version)] = item;
        return Task.CompletedTask;
    }

    public Task<TalentTemplate?> FindAsync(string templateId, int? version = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);

        if (version.HasValue)
        {
            _templates.TryGetValue((templateId, version.Value), out var found);
            return Task.FromResult<TalentTemplate?>(found);
        }

        var latest = _templates.Values
            .Where(t => t.TemplateId.Equals(templateId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.Version)
            .FirstOrDefault();

        return Task.FromResult<TalentTemplate?>(latest);
    }

    public Task<IReadOnlyList<TalentTemplate>> ListAsync(TalentTemplateStatus? status = null, CancellationToken ct = default)
    {
        var query = _templates.Values.AsEnumerable();
        if (status.HasValue)
        {
            query = query.Where(t => t.Status == status.Value);
        }

        // Return latest version per templateId
        var result = query
            .GroupBy(t => t.TemplateId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(t => t.Version).First())
            .OrderBy(t => t.TemplateId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Task.FromResult<IReadOnlyList<TalentTemplate>>(result);
    }

    public Task<int> GetNextVersionAsync(string templateId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);

        var maxVersion = _templates.Values
            .Where(t => t.TemplateId.Equals(templateId, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Version)
            .DefaultIfEmpty(0)
            .Max();

        return Task.FromResult(maxVersion + 1);
    }

    public Task<bool> UpdateStatusAsync(
        string templateId,
        int version,
        TalentTemplateStatus newStatus,
        string? approvedBy = null,
        DateTimeOffset? activatedAt = null,
        CancellationToken ct = default)
    {
        if (!_templates.TryGetValue((templateId, version), out var existing))
        {
            return Task.FromResult(false);
        }

        var updated = existing with
        {
            Status = newStatus,
            ApprovedBy = approvedBy ?? existing.ApprovedBy,
            ActivatedAt = activatedAt ?? existing.ActivatedAt,
        };

        _templates[(templateId, version)] = updated;
        return Task.FromResult(true);
    }
}
