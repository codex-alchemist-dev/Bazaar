using System.Text;

namespace Bazaar.Archive;

/// <summary>
/// Rebuilds the search index from the bundles in the store (doc 25 "Archive cold storage": the index is rebuildable from the bundles).
/// Runs once, on the first search after a start. A bundle that fails verify is left out, so search never shows text the hashes do not vouch for.
/// </summary>
public sealed class ArchiveIndexRebuilder(IArchiveStore store, ArchiveVerifier verifier, IArchiveSearchIndex index)
{
    private readonly Lock _gate = new();
    private Task? _build;

    /// <summary>Builds the index the first time it is called; later calls share that one build.</summary>
    public Task EnsureBuiltAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            // A cancelled or failed build is retried by the next call.
            if (_build is { IsCompletedSuccessfully: false, IsCompleted: true })
            {
                _build = null;
            }

            return _build ??= BuildAsync(ct);
        }
    }

    private async Task BuildAsync(CancellationToken ct)
    {
        foreach (var unit in await store.ListUnitsAsync(ct))
        {
            var verified = await verifier.VerifyAsync(unit, ct);
            if (!verified.Ok || verified.Manifest is null)
            {
                continue;
            }

            List<ArchiveRecord> records = [];
            foreach (var seg in verified.Manifest.Segments)
            {
                var bytes = await store.GetAsync(unit, seg.Name, ct);
                if (bytes is null)
                {
                    continue;
                }

                foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    records.Add(ArchiveHashing.FromLine(line));
                }
            }

            await index.IndexAsync(unit, records, ct);
        }
    }
}
