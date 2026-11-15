namespace Bazaar.Portability.Client;

/// <summary>Sample items for one target (a record kind), capped at <see cref="PreviewSummary.SampleLimit"/>.</summary>
public sealed record TargetPreview(string Target, int Count, IReadOnlyList<SourceRecord> Samples);

/// <summary>Two or more new records with the same target and title. Each source key is listed.</summary>
public sealed record PreviewCollision(string Target, string Title, IReadOnlyList<string> SourceKeys);

/// <summary>A new record the mapping cannot place, with the reason.</summary>
public sealed record UnmappedItem(string SourceKey, string Reason);

/// <summary>Read-only view of an import preview: counts, samples, collisions and unmapped items.</summary>
public sealed record PreviewSummary(
    int NewCount,
    int DuplicateCount,
    IReadOnlyList<TargetPreview> Targets,
    IReadOnlyList<PreviewCollision> Collisions,
    IReadOnlyList<UnmappedItem> Unmapped)
{
    public const int SampleLimit = 20;

    /// <summary>Builds the summary from a planner output. Pure.</summary>
    public static PreviewSummary From(ImportPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);

        var targets = preview.New
            .Where(r => r.Kind.Length > 0)
            .GroupBy(r => r.Kind, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new TargetPreview(g.Key, g.Count(), g.Take(SampleLimit).ToList()))
            .ToList();

        var collisions = preview.New
            .Where(r => r.Kind.Length > 0 && r.Title.Length > 0)
            .GroupBy(r => (r.Kind, r.Title))
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key.Kind, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Title, StringComparer.Ordinal)
            .Select(g => new PreviewCollision(g.Key.Kind, g.Key.Title, g.Select(r => r.SourceKey).ToList()))
            .ToList();

        var unmapped = new List<UnmappedItem>();
        foreach (var record in preview.New)
        {
            if (record.Kind.Length == 0) unmapped.Add(new UnmappedItem(record.SourceKey, "no kind"));
            else if (record.Title.Length == 0) unmapped.Add(new UnmappedItem(record.SourceKey, "no title"));
        }

        return new PreviewSummary(preview.New.Count, preview.Duplicates.Count, targets, collisions, unmapped);
    }
}
