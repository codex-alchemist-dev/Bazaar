using System.Runtime.CompilerServices;
using System.Text.Json;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Read;

namespace Bazaar.Server.Portability;

/// <summary>The outcome of one GC export. <see cref="Complete"/> is true only when the self-check passed.</summary>
/// <param name="GcId">The group chat exported.</param>
/// <param name="HeadSeq">The head seq the export was taken at.</param>
/// <param name="Files">The files written, sorted by path.</param>
/// <param name="SelfCheck">The re-read of the written files (doc 44 section 6.2).</param>
public sealed record GcExportResult(Guid GcId, long HeadSeq, IReadOnlyList<ExportedFile> Files, GcExportSelfCheckResult SelfCheck)
{
    /// <summary>A GC export that fails its self-check is incomplete and is not offered for download (doc 44 section 6.2).</summary>
    public bool Complete => SelfCheck.Passed;
}

/// <summary>
/// Exports one GC as <c>gc.jsonl.v1</c> (doc 44 section 4.2): events, messages, message versions and <c>head.json</c>.
/// </summary>
/// <remarks>
/// The export reads only through <see cref="IGcLogReader"/> and <see cref="IGcMessageVersionSource"/>, in pages, and writes each
/// line as it is read, so the whole log is never held in memory. The head is read first and fixes the export point. Events and
/// versions committed after it are left out, so the export is one consistent prefix even while the GC is live. Bodies are copied
/// as ciphertext with their key id. This class has no body codec and never decrypts (doc 44 sections 4.6 and 10). Output is
/// deterministic: the same log gives the same bytes.
/// </remarks>
public sealed class GcJsonlExporter
{
    private readonly IGcLogReader _log;
    private readonly IGcMessageVersionSource _versions;
    private readonly int _pageSize;

    /// <param name="log">The GC log reader. Viewer rules are applied by the reader.</param>
    /// <param name="versions">The message version reader.</param>
    /// <param name="pageSize">Rows read per page. Bounds the memory an export holds at any moment.</param>
    public GcJsonlExporter(IGcLogReader log, IGcMessageVersionSource versions, int pageSize = 500)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, IGcLogReader.MaxLimit);
        _log = log;
        _versions = versions;
        _pageSize = pageSize;
    }

    /// <summary>The exporter's id, as the format registry names it.</summary>
    public static string Id => GcJsonlFormat.FormatId;

    /// <summary>The format's schema version, written into every head.json.</summary>
    public static string Version => GcJsonlFormat.SchemaVersion;

    /// <summary>
    /// Writes the export for one GC into <paramref name="store"/>, then re-reads it with <see cref="GcExportSelfCheck"/>.
    /// Throws <see cref="GcAccessDeniedException"/> when the viewer may not read the GC.
    /// </summary>
    public async Task<GcExportResult> ExportAsync(Guid gcId, GcPrincipal viewer, IExportFileStore store, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(store);

        // The head is the export point. Anything committed after this read is left out.
        var head = await _log.GetHeadSeqAsync(gcId, ct);

        var eventsFile = GcJsonlFormat.PathFor(gcId, GcJsonlFormat.EventsFile);
        var messagesFile = GcJsonlFormat.PathFor(gcId, GcJsonlFormat.MessagesFile);
        var versionsFile = GcJsonlFormat.PathFor(gcId, GcJsonlFormat.VersionsFile);
        var headFile = GcJsonlFormat.PathFor(gcId, GcJsonlFormat.HeadFile);

        await using (var events = await GcJsonlFileWriter.CreateAsync(store, eventsFile, ct))
        await using (var messages = await GcJsonlFileWriter.CreateAsync(store, messagesFile, ct))
        await using (var rows = await GcJsonlFileWriter.CreateAsync(store, versionsFile, ct))
        {
            await foreach (var ev in ReadEventsAsync(gcId, head, viewer, ct))
            {
                await events.WriteLineAsync(w => WriteEvent(w, ev), ct);
                if (ev.Kind == GcEventKinds.Message)
                {
                    await messages.WriteLineAsync(w => WriteMessage(w, ev), ct);
                }
            }

            await foreach (var row in ReadVersionsAsync(gcId, head, viewer, ct))
            {
                await rows.WriteLineAsync(w => WriteVersion(w, row), ct);
            }

            var files = new List<ExportedFile>
            {
                await events.FinishAsync(ct),
                await messages.FinishAsync(ct),
                await rows.FinishAsync(ct),
            };

            var headRecord = new GcExportHead(
                gcId,
                head,
                events.Count,
                messages.Count,
                rows.Count,
                events.PrevHash,
                messages.PrevHash,
                rows.PrevHash,
                files);
            await WriteHeadAsync(store, headFile, headRecord.ToBytes(), ct);
        }

        // The files are closed; re-read them from the store, not from memory.
        var check = await GcExportSelfCheck.VerifyAsync(store, gcId, ct);
        var written = await ListWrittenAsync(store, gcId, ct);
        return new GcExportResult(gcId, head, written, check);
    }

    /// <summary>Events from seq 1 up to the head, read in pages. The reader's order is checked, since the export depends on it.</summary>
    private async IAsyncEnumerable<GcEvent> ReadEventsAsync(
        Guid gcId, long head, GcPrincipal viewer, [EnumeratorCancellation] CancellationToken ct)
    {
        var after = 0L;
        while (after < head)
        {
            var read = 0;
            await foreach (var ev in _log.ReadAsync(gcId, after, _pageSize, viewer, ct))
            {
                read++;
                if (ev.Seq > head)
                {
                    yield break;
                }

                if (ev.Seq <= after)
                {
                    throw new InvalidOperationException($"GC {gcId} returned seq {ev.Seq} after seq {after}; the log is out of order.");
                }

                after = ev.Seq;
                yield return ev;
            }

            // An empty page means the viewer's grant or the log ends here. The self-check reports the shortfall against the head.
            if (read == 0)
            {
                yield break;
            }
        }
    }

    /// <summary>Message versions up to the head, read in pages with the same order check as the events.</summary>
    private async IAsyncEnumerable<GcMessageVersionRecord> ReadVersionsAsync(
        Guid gcId, long head, GcPrincipal viewer, [EnumeratorCancellation] CancellationToken ct)
    {
        var after = 0L;
        while (after < head)
        {
            var read = 0;
            await foreach (var row in _versions.ReadAsync(gcId, after, _pageSize, viewer, ct))
            {
                read++;
                if (row.Seq > head)
                {
                    yield break;
                }

                if (row.Seq < after)
                {
                    throw new InvalidOperationException($"GC {gcId} returned a version at seq {row.Seq} after seq {after}; versions are out of order.");
                }

                after = row.Seq;
                yield return row;
            }

            if (read == 0)
            {
                yield break;
            }
        }
    }

    private static void WriteEvent(Utf8JsonWriter w, GcEvent ev)
    {
        w.WriteNumber("seq", ev.Seq);
        w.WriteString("eventId", ev.EventId.ToString("D"));
        w.WriteString("kind", ev.Kind);
        w.WriteString("authorKind", GcJsonlFormat.WireName(ev.AuthorKind));
        w.WriteString("authorId", ev.AuthorId.ToString("D"));
        w.WriteString("sourceTrust", GcJsonlFormat.WireName(ev.SourceTrust));
        w.WriteBoolean("taintedAtWrite", ev.TaintedAtWrite);
        GcJsonlFormat.WriteCanonical(w, "taintSources", ev.TaintSourcesJson);
        GcJsonlFormat.WriteOptional(w, "messageId", ev.MessageId);
        GcJsonlFormat.WriteOptional(w, "version", ev.Version);
        GcJsonlFormat.WriteOptional(w, "targetSeq", ev.TargetSeq);
        GcJsonlFormat.WriteOptional(w, "replyToSeq", ev.ReplyToSeq);
        GcJsonlFormat.WriteCanonical(w, "payload", ev.PayloadJson);
        w.WriteString("createdAt", GcJsonlFormat.FormatTime(ev.CreatedAt));
    }

    private static void WriteMessage(Utf8JsonWriter w, GcEvent ev)
    {
        if (ev.MessageId is not { } messageId)
        {
            throw new InvalidOperationException($"Message event at seq {ev.Seq} has no message id.");
        }

        w.WriteString("messageId", messageId.ToString("D"));
        w.WriteNumber("firstSeq", ev.Seq);
        w.WriteString("authorKind", GcJsonlFormat.WireName(ev.AuthorKind));
        w.WriteString("authorId", ev.AuthorId.ToString("D"));
        w.WriteString("createdAt", GcJsonlFormat.FormatTime(ev.CreatedAt));
    }

    private static void WriteVersion(Utf8JsonWriter w, GcMessageVersionRecord row)
    {
        if (row.Tombstone)
        {
            if (row.Ciphertext is not null)
            {
                throw new InvalidOperationException($"Tombstone version at seq {row.Seq} carries a body; refusing to export it.");
            }
        }
        else if (row.KeyId is null || row.Nonce is null || row.Ciphertext is null)
        {
            throw new InvalidOperationException($"Version at seq {row.Seq} has no sealed body or key id.");
        }

        w.WriteNumber("seq", row.Seq);
        w.WriteString("messageId", row.MessageId.ToString("D"));
        w.WriteNumber("version", row.Version);
        w.WriteBoolean("editMarker", row.Version > 1);
        w.WriteString("editorId", row.EditorId.ToString("D"));
        w.WriteString("editedAt", GcJsonlFormat.FormatTime(row.CreatedAt));
        w.WriteBoolean("tombstone", row.Tombstone);
        if (row.Tombstone)
        {
            w.WriteNull("body");
            return;
        }

        // Ciphertext as stored, with the key id and nonce needed to open it later. Nothing is opened here.
        w.WriteStartObject("body");
        w.WriteString("keyId", row.KeyId!.Value.ToString("D"));
        w.WriteBase64String("nonce", row.Nonce);
        w.WriteBase64String("ciphertext", row.Ciphertext);
        w.WriteEndObject();
    }

    private static async Task WriteHeadAsync(IExportFileStore store, string path, byte[] bytes, CancellationToken ct)
    {
        await using var stream = await store.CreateAsync(path, ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>Lists the files the export wrote, read back from the store, sorted by path.</summary>
    private static async Task<IReadOnlyList<ExportedFile>> ListWrittenAsync(IExportFileStore store, Guid gcId, CancellationToken ct)
    {
        var files = new List<ExportedFile>();
        foreach (var name in new[] { GcJsonlFormat.EventsFile, GcJsonlFormat.MessagesFile, GcJsonlFormat.VersionsFile, GcJsonlFormat.HeadFile })
        {
            var path = GcJsonlFormat.PathFor(gcId, name);
            await using var stream = await store.OpenReadAsync(path, ct);
            files.Add(await GcJsonlFormat.HashAsync(path, stream, ct));
        }

        return files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
    }
}
