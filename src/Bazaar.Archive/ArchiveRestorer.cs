using System.Text;

namespace Bazaar.Archive;

/// <summary>Verifies a bundle, then writes its records through the owner's restore target. Never overwrites a live record.</summary>
public sealed class ArchiveRestorer(IArchiveStore store, IArchiveRestoreTarget target)
{
    public async Task<ArchiveRestoreResult> RestoreAsync(ArchiveUnit unit, CancellationToken ct)
    {
        var verified = await new ArchiveVerifier(store).VerifyAsync(unit, ct);
        if (!verified.Ok || verified.Manifest is null)
        {
            throw new InvalidOperationException("Archive failed verify: " + string.Join("; ", verified.Problems));
        }

        var restored = 0;
        var present = 0;
        var conflicts = new List<string>();
        foreach (var seg in verified.Manifest.Segments)
        {
            var bytes = (await store.GetAsync(unit, seg.Name, ct))!;
            foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var record = ArchiveHashing.FromLine(line);
                var live = await target.GetLiveHashAsync(unit, record.Id, ct);
                if (live is null)
                {
                    await target.WriteAsync(unit, record, ct);
                    restored++;
                }
                else if (string.Equals(live, ArchiveHashing.RecordHash(record), StringComparison.Ordinal))
                {
                    present++;
                }
                else
                {
                    conflicts.Add(record.Id);
                }
            }
        }

        return new ArchiveRestoreResult(restored, present, conflicts);
    }
}
