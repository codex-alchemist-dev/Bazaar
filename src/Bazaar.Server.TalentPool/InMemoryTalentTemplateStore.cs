namespace Bazaar.Server.TalentPool;

/// <summary>In-memory template store for tests and development. Append only.</summary>
public sealed class InMemoryTalentTemplateStore : ITalentTemplateStore
{
    private readonly Dictionary<string, List<TalentTemplate>> _byId = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <inheritdoc />
    public Task AddAsync(TalentTemplate version, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(version);
        lock (_gate)
        {
            if (!_byId.TryGetValue(version.TemplateId, out var list))
            {
                _byId[version.TemplateId] = list = [];
            }

            if (list.Any(t => t.Version == version.Version))
            {
                throw new InvalidOperationException($"{version.HireKey} already exists.");
            }

            list.Add(version);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<TalentTemplate?> FindAsync(string templateId, int? version, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(templateId, out var list))
            {
                return Task.FromResult<TalentTemplate?>(null);
            }

            var found = version is null ? list.MaxBy(t => t.Version) : list.FirstOrDefault(t => t.Version == version);
            return Task.FromResult(found);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TalentTemplate>> ListLatestAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<TalentTemplate> all = _byId.Values.Select(l => l.MaxBy(t => t.Version)!).OrderBy(t => t.TemplateId, StringComparer.Ordinal).ToArray();
            return Task.FromResult(all);
        }
    }
}
