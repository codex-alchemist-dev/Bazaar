using Bazaar.Contracts.Gc;

namespace Bazaar.Server.Portability;

/// <summary>
/// Runs <see cref="GcJsonlExporter"/> for each requested GC into the bundle (doc 44 sections 4.2 and 5): <c>events.jsonl</c>,
/// <c>messages.jsonl</c>, <c>versions.jsonl</c>, <c>mentions.jsonl</c> (when the exporter was given a mention reader) and
/// <c>head.json</c>, under <c>gc/&lt;gc_id&gt;/</c>. Id <c>gc.jsonl.v1</c>.
/// </summary>
/// <remarks>
/// The exporter re-reads its own files for the section 6.2 self-check, and a sink is write-only, so each GC is exported into a
/// scratch directory first, checked, then copied into the sink. A GC whose self-check fails throws and aborts the bundle. The
/// scratch directory is removed in every case. Memory stays flat: files are streamed, never held whole. Do not combine this source
/// with <see cref="GcMentionsBundleSource"/> when the exporter already writes mentions; both would write the same path.
/// </remarks>
public sealed class GcBundleSource : IBundleSource
{
    public const string SourceId = GcJsonlFormat.FormatId;

    private readonly GcJsonlExporter _exporter;
    private readonly GcPrincipal _viewer;
    private readonly IReadOnlyList<Guid> _gcIds;
    private readonly string? _scratchRoot;

    /// <param name="exporter">The GC exporter. Give it an <see cref="IGcMentionSource"/> for a full bundle.</param>
    /// <param name="viewer">Whose rights the export follows (doc 44 section 4.1).</param>
    /// <param name="gcIds">The GCs to export. Order does not matter; they are written in id order.</param>
    /// <param name="scratchRoot">Where the scratch directory goes. Null uses the system temp folder.</param>
    public GcBundleSource(GcJsonlExporter exporter, GcPrincipal viewer, IEnumerable<Guid> gcIds, string? scratchRoot = null)
    {
        ArgumentNullException.ThrowIfNull(exporter);
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(gcIds);
        _exporter = exporter;
        _viewer = viewer;
        _gcIds = gcIds.Distinct().Order().ToList();
        _scratchRoot = scratchRoot;
    }

    public string Id => SourceId;

    /// <summary>The per-GC results from the last run, in id order, empty before the first run.</summary>
    public IReadOnlyList<GcExportResult> Results { get; private set; } = [];

    public async Task ExportAsync(IExportFileSink sink, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var results = new List<GcExportResult>();
        foreach (var gcId in _gcIds)
        {
            ct.ThrowIfCancellationRequested();
            var scratch = Path.Combine(_scratchRoot ?? Path.GetTempPath(), "bazaar-gc-export-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(scratch);
                var store = new DirectoryExportFileStore(scratch);
                var result = await _exporter.ExportAsync(gcId, _viewer, store, ct);
                if (!result.Complete)
                {
                    throw new InvalidOperationException(
                        $"GC {gcId:D} failed its export self-check and is incomplete: " + string.Join(" ", result.SelfCheck.Problems));
                }

                foreach (var file in result.Files)
                {
                    await using var input = await store.OpenReadAsync(file.Path, ct);
                    await using var output = await sink.OpenAsync(file.Path, ct);
                    await input.CopyToAsync(output, ct);
                }

                results.Add(result);
            }
            finally
            {
                if (Directory.Exists(scratch))
                {
                    Directory.Delete(scratch, recursive: true);
                }
            }
        }

        Results = results;
    }

    /// <summary>An <see cref="IExportFileStore"/> over a scratch directory. Paths are the exporter's own, so they are checked.</summary>
    private sealed class DirectoryExportFileStore(string root) : IExportFileStore
    {
        public Task<Stream> CreateAsync(string path, CancellationToken ct)
        {
            var full = Resolve(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            return Task.FromResult<Stream>(new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, useAsync: true));
        }

        public Task<Stream> OpenReadAsync(string path, CancellationToken ct)
        {
            var full = Resolve(path);
            if (!File.Exists(full))
            {
                throw new FileNotFoundException("No such export file.", path);
            }

            return Task.FromResult<Stream>(new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, useAsync: true));
        }

        private string Resolve(string path)
        {
            if (string.IsNullOrEmpty(path) || path.StartsWith('/') || path.Contains('\\') || path.Split('/').Any(p => p is "" or "." or ".."))
            {
                throw new ArgumentException("Bad export path: " + path, nameof(path));
            }

            return Path.Combine(root, path);
        }
    }
}
