using System.Text.Json;

namespace Bazaar.Archive;

/// <summary>Recomputes every hash of a bundle against its manifest. Fails closed.</summary>
public sealed class ArchiveVerifier(IArchiveStore store)
{
    public async Task<ArchiveVerifyResult> VerifyAsync(ArchiveUnit unit, CancellationToken ct)
    {
        var problems = new List<string>();
        var raw = await store.GetAsync(unit, ArchiveHashing.ManifestName, ct);
        if (raw is null)
        {
            return new ArchiveVerifyResult(false, ["manifest missing"], null);
        }

        ArchiveManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(raw, ArchiveJson.Default.ArchiveManifest);
        }
        catch (JsonException)
        {
            return new ArchiveVerifyResult(false, ["manifest unreadable"], null);
        }

        if (manifest is null || manifest.FormatVersion != ArchiveHashing.FormatVersion)
        {
            return new ArchiveVerifyResult(false, ["manifest has an unsupported format version"], manifest);
        }

        var count = 0;
        foreach (var seg in manifest.Segments)
        {
            var bytes = await store.GetAsync(unit, seg.Name, ct);
            if (bytes is null)
            {
                problems.Add($"segment {seg.Name} missing");
                continue;
            }

            if (bytes.Length != seg.Length || ArchiveHashing.Sha256Hex(bytes) != seg.Sha256)
            {
                problems.Add($"segment {seg.Name} hash mismatch");
            }

            count += seg.RecordCount;
        }

        if (ArchiveHashing.RootHash(manifest.Segments) != manifest.RootHash)
        {
            problems.Add("root hash mismatch");
        }

        if (count != manifest.RecordCount)
        {
            problems.Add("record count mismatch");
        }

        var listed = manifest.Segments.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in await store.ListAsync(unit, ct))
        {
            if (name != ArchiveHashing.ManifestName && !listed.Contains(name))
            {
                problems.Add($"unlisted file {name}");
            }
        }

        return new ArchiveVerifyResult(problems.Count == 0, problems, manifest);
    }
}
