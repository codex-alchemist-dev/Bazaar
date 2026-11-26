using System.Text;
using System.Text.Json;

namespace Bazaar.Archive;

/// <summary>
/// Copies a unit into cold storage (doc 25 "Archive cold storage"): JSON Lines segments plus a manifest with hashes, a
/// verify pass, then it marks the source archived. It never deletes the source, and thinking text is refused.
/// </summary>
public sealed class ColdArchiver(IArchiveSource source, IArchiveStore store, IArchiveSearchIndex? index, ArchiveOptions options, TimeProvider clock)
{
    public const string ThinkingKind = "thinking";

    public async Task<ArchiveManifest> ArchiveAsync(ArchiveUnit unit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (!ArchiveHashing.IsSafeId(unit.Id))
        {
            throw new ArgumentException("Unit id must be lowercase letters, digits, hyphens and dots.", nameof(unit));
        }

        if (await store.GetAsync(unit, ArchiveHashing.ManifestName, ct) is not null)
        {
            throw new InvalidOperationException($"Unit '{unit.Id}' is already archived.");
        }

        var segments = new List<ArchiveSegment>();
        var indexed = new List<ArchiveRecord>();
        var current = new StringBuilder();
        var inSegment = 0;
        var total = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        async Task FlushAsync()
        {
            if (inSegment == 0)
            {
                return;
            }

            var bytes = new UTF8Encoding(false).GetBytes(current.ToString());
            var name = $"segments/{segments.Count + 1:D4}.jsonl";
            await store.PutAsync(unit, name, bytes, ct);
            segments.Add(new ArchiveSegment(name, ArchiveHashing.Sha256Hex(bytes), bytes.Length, inSegment));
            current.Clear();
            inSegment = 0;
        }

        await foreach (var record in source.ReadAsync(unit, ct))
        {
            if (string.Equals(record.Kind, ThinkingKind, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Record '{record.Id}' is thinking text, which is never archived (doc 25 floors).");
            }

            if (!seen.Add(record.Id))
            {
                throw new InvalidOperationException($"Duplicate record id '{record.Id}'.");
            }

            current.Append(ArchiveHashing.ToLine(record)).Append('\n');
            inSegment++;
            total++;
            indexed.Add(record);
            if (inSegment >= options.SegmentMaxRecords)
            {
                await FlushAsync();
            }
        }

        await FlushAsync();

        var manifest = new ArchiveManifest(
            ArchiveHashing.FormatVersion, unit, clock.GetUtcNow(), total, segments, ArchiveHashing.RootHash(segments));
        await store.PutAsync(unit, ArchiveHashing.ManifestName, JsonSerializer.SerializeToUtf8Bytes(manifest, ArchiveJson.Default.ArchiveManifest), ct);

        var verified = await new ArchiveVerifier(store).VerifyAsync(unit, ct);
        if (!verified.Ok)
        {
            throw new InvalidOperationException("Archive failed its own verify: " + string.Join("; ", verified.Problems));
        }

        if (index is not null)
        {
            await index.IndexAsync(unit, indexed, ct);
        }

        // Last, so a failure above leaves the source untouched.
        await source.MarkArchivedAsync(unit, manifest.RootHash, ct);
        return manifest;
    }
}

/// <summary>Cold-storage settings (doc 25: <c>archive.segment_max_records</c>).</summary>
public sealed record ArchiveOptions(int SegmentMaxRecords = 5000);
