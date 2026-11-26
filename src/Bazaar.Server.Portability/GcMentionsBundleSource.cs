using System.Runtime.CompilerServices;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Read;

namespace Bazaar.Server.Portability;

/// <summary>
/// One <c>gc_mention</c> row (docs/design/01-gc-data-model.md, doc 04): the event at <paramref name="Seq"/> mentioned
/// <paramref name="TargetPrincipalId"/>. The table holds nothing more.
/// </summary>
public sealed record GcMentionRecord(long Seq, Guid TargetPrincipalId);

/// <summary>
/// The read side the mentions export needs (doc 44 section 4.2, <c>mentions.jsonl</c>). A seq can carry several mentions, so a page
/// is addressed by the last row read, not by seq alone. The host implements this over <c>gc_mention</c>.
/// </summary>
public interface IGcMentionSource
{
    /// <summary>
    /// Mentions that sort after (<paramref name="afterSeq"/>, <paramref name="afterTarget"/>), ordered by seq then target id, at most
    /// <paramref name="limit"/> of them, none past the viewer's grant. A first page passes seq 0 and <see cref="Guid.Empty"/>. Throws
    /// <c>GcAccessDeniedException</c> when the viewer may not read the GC.
    /// </summary>
    IAsyncEnumerable<GcMentionRecord> ReadAsync(
        Guid gcId, long afterSeq, Guid afterTarget, int limit, GcPrincipal viewer, CancellationToken ct);
}

/// <summary>
/// Writes <c>gc/&lt;gc_id&gt;/mentions.jsonl</c> for each requested GC (doc 44 section 4.2), so a reader can rebuild @mention history
/// without the database. Id <c>gc.mentions.v1</c>.
/// </summary>
/// <remarks>
/// Each line is a sealed hash-chain line like the other <c>gc.jsonl.v1</c> files (<see cref="GcJsonlFormat"/>): fields
/// <c>seq</c> and <c>targetPrincipalId</c>, then <c>prevHash</c> and <c>hash</c>. The head seq is read first and fixes the export
/// point, as <see cref="GcJsonlExporter"/> does, so mentions committed later are left out. A GC with no mentions still gets an
/// empty file. This source does not write <c>head.json</c> and does not change the files <see cref="GcJsonlExporter"/> writes.
/// </remarks>
public sealed class GcMentionsBundleSource : IBundleSource
{
    public const string SourceId = "gc.mentions.v1";

    /// <summary>The file name inside each GC folder.</summary>
    public const string MentionsFile = "mentions.jsonl";

    private readonly IGcLogReader _log;
    private readonly IGcMentionSource _mentions;
    private readonly GcPrincipal _viewer;
    private readonly IReadOnlyList<Guid> _gcIds;
    private readonly int _pageSize;

    /// <param name="log">Used only for each GC's head seq.</param>
    /// <param name="mentions">The mention reader. Viewer rules are applied by the reader.</param>
    /// <param name="viewer">Whose rights the export follows (doc 44 section 4.1).</param>
    /// <param name="gcIds">The GCs to export. Order does not matter; files are written in id order.</param>
    /// <param name="pageSize">Rows read per page. Bounds the memory an export holds at any moment.</param>
    public GcMentionsBundleSource(
        IGcLogReader log, IGcMentionSource mentions, GcPrincipal viewer, IEnumerable<Guid> gcIds, int pageSize = 500)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(mentions);
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(gcIds);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, IGcLogReader.MaxLimit);
        _log = log;
        _mentions = mentions;
        _viewer = viewer;
        _gcIds = gcIds.Distinct().Order().ToList();
        _pageSize = pageSize;
    }

    public string Id => SourceId;

    /// <summary>Mention counts per GC from the last run, empty before the first run.</summary>
    public IReadOnlyDictionary<Guid, long> Counts { get; private set; } = new Dictionary<Guid, long>();

    public async Task ExportAsync(IExportFileSink sink, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var counts = new Dictionary<Guid, long>();
        foreach (var gcId in _gcIds)
        {
            ct.ThrowIfCancellationRequested();
            counts[gcId] = await WriteOneAsync(sink, gcId, ct);
        }

        Counts = counts;
    }

    private async Task<long> WriteOneAsync(IExportFileSink sink, Guid gcId, CancellationToken ct)
    {
        var head = await _log.GetHeadSeqAsync(gcId, ct);
        var prevHash = GcJsonlFormat.GenesisHash;
        long count = 0;
        await using var stream = await sink.OpenAsync(GcJsonlFormat.PathFor(gcId, MentionsFile), ct);
        await foreach (var mention in ReadAsync(gcId, head, ct))
        {
            var line = GcJsonlFormat.Seal(prevHash, w =>
            {
                w.WriteNumber("seq", mention.Seq);
                w.WriteString("targetPrincipalId", mention.TargetPrincipalId.ToString("D"));
            });
        await foreach (var mention in ReadPagedAsync(_mentions, gcId, head, _pageSize, _viewer, ct))
        {
            var line = GcJsonlFormat.Seal(prevHash, w => WriteMention(w, mention));
            await stream.WriteAsync(line.Bytes, ct);
            prevHash = line.Hash;
            count++;
        }

        await stream.FlushAsync(ct);
        return count;
    }

    /// <summary>Mentions up to the head, read in pages. The reader's order is checked, since the export depends on it.</summary>
    private async IAsyncEnumerable<GcMentionRecord> ReadAsync(Guid gcId, long head, [EnumeratorCancellation] CancellationToken ct)
    /// <summary>The fields of one mention line, shared with <see cref="GcJsonlExporter"/>.</summary>
    internal static void WriteMention(System.Text.Json.Utf8JsonWriter w, GcMentionRecord mention)
    {
        w.WriteNumber("seq", mention.Seq);
        w.WriteString("targetPrincipalId", mention.TargetPrincipalId.ToString("D"));
    }

    /// <summary>Mentions up to the head, read in pages. The reader's order is checked, since the export depends on it.</summary>
    internal static async IAsyncEnumerable<GcMentionRecord> ReadPagedAsync(
        IGcMentionSource mentions, Guid gcId, long head, int pageSize, GcPrincipal viewer, [EnumeratorCancellation] CancellationToken ct)
    {
        var afterSeq = 0L;
        var afterTarget = Guid.Empty;
        var first = true;
        // Not bounded by seq: several mentions can share the head seq and a page can end between them.
        while (true)
        {
            var read = 0;
            await foreach (var row in _mentions.ReadAsync(gcId, afterSeq, afterTarget, _pageSize, _viewer, ct))
            await foreach (var row in mentions.ReadAsync(gcId, afterSeq, afterTarget, pageSize, viewer, ct))
            {
                read++;
                if (row.Seq > head)
                {
                    yield break;
                }

                // Same seq is allowed (several mentions on one event); the same pair twice, or a lower seq, means the reader is wrong.
                if (row.Seq < afterSeq || (!first && row.Seq == afterSeq && row.TargetPrincipalId == afterTarget))
                {
                    throw new InvalidOperationException(
                        $"GC {gcId} returned a mention at seq {row.Seq} after seq {afterSeq}; mentions are out of order.");
                }

                first = false;
                afterSeq = row.Seq;
                afterTarget = row.TargetPrincipalId;
                yield return row;
            }

            // A short page means the viewer's grant or the rows end here.
            if (read < _pageSize)
            if (read < pageSize)
            {
                yield break;
            }
        }
    }
}
