namespace Bazaar.Archive;

/// <summary>Term search over archived records: every query term must appear (case-insensitive). Rebuildable from the bundles.</summary>
public sealed class InMemoryArchiveSearchIndex : IArchiveSearchIndex
{
    private readonly Lock _gate = new();
    private readonly Dictionary<ArchiveUnit, IReadOnlyList<ArchiveRecord>> _units = [];

    public Task IndexAsync(ArchiveUnit unit, IReadOnlyList<ArchiveRecord> records, CancellationToken ct)
    {
        lock (_gate)
        {
            _units[unit] = records;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ArchiveSearchHit>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<ArchiveSearchHit> hits = [];
        if (terms.Length > 0 && limit > 0)
        {
            lock (_gate)
            {
                foreach (var (unit, records) in _units)
                {
                    hits.AddRange(records
                        .Where(r => terms.All(t => r.Body.Contains(t, StringComparison.OrdinalIgnoreCase)))
                        .Select(r => new ArchiveSearchHit(unit, r.Id, r.At, Snippet(r.Body))));
                }
            }
        }

        return Task.FromResult<IReadOnlyList<ArchiveSearchHit>>(
            hits.OrderBy(h => h.Unit.Id, StringComparer.Ordinal).ThenBy(h => h.At).ThenBy(h => h.RecordId, StringComparer.Ordinal).Take(limit).ToList());
    }

    private static string Snippet(string body) => body.Length <= 160 ? body : body[..160];
}
