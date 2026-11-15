using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Gc.Write;

namespace Bazaar.Portability;

/// <summary>
/// The GC append operation the import needs. It has the same shape as the batch append on the GC log writer
/// (<c>AppendManyAsync</c>), so a host adapts its writer with one call. Duplicate client message ids return the original event.
/// </summary>
public interface IGcAppender
{
    Task<IReadOnlyList<AppendResult>> AppendManyAsync(IReadOnlyList<AppendRequest> requests, CancellationToken ct);
}

/// <summary>
/// Writes import batches as GC messages through the GC abstractions (doc 44 section 8.2, GC entry rule). Bodies are sealed
/// by <see cref="IGcBodyCodec"/> before they reach the log. The idempotency key is derived from the source key, so a re-run
/// that appends the same record gets the original event back.
/// </summary>
public sealed class GcImportTarget(
    IGcAppender appender,
    IGcBodyCodec codec,
    IGcLogReader reader,
    Guid gcId,
    Guid authorId,
    AuthorKind authorKind,
    GcPrincipal viewer)
{
    /// <summary>Appends one batch as a single append call. Throws when the batch cannot be appended.</summary>
    public async Task AppendBatchAsync(string jobId, int batchIndex, IReadOnlyList<ImportItem> items, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(items);
        var requests = new List<AppendRequest>(items.Count);
        foreach (var item in items)
        {
            var body = await codec.SealAsync(authorId, item.Record.Body, ct);
            var payload = PayloadFor(jobId, batchIndex, item.Record);
            if (item.Replaces is null)
            {
                requests.Add(new AppendRequest(gcId, GcEventKinds.Message, authorKind, authorId, SourceTrust.External, ClientMsgIdFor(item.Provenance.SourceKey))
                {
                    Message = body,
                    PayloadJson = payload,
                });
                continue;
            }

            // A replace appends a new version of the stored message. Earlier versions stay in the log (doc 01, edits).
            var stored = item.Replaces;
            if (!string.Equals(stored.Owner, authorId.ToString("D"), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Replace refused: the stored message was written by another author.");
            }
            var version = int.Parse(stored.Version, CultureInfo.InvariantCulture) + 1;
            requests.Add(new AppendRequest(gcId, GcEventKinds.MessageEdit, authorKind, authorId, SourceTrust.External, ClientMsgIdFor(item.Provenance.SourceKey) + ":v" + version.ToString(CultureInfo.InvariantCulture))
            {
                Message = body,
                MessageId = Guid.Parse(stored.ObjectId),
                Version = version,
                PayloadJson = payload,
            });
        }

        var results = await appender.AppendManyAsync(requests, ct);
        if (results.Count != requests.Count)
        {
            throw new InvalidOperationException("GC append returned " + results.Count.ToString(CultureInfo.InvariantCulture)
                + " results for " + requests.Count.ToString(CultureInfo.InvariantCulture) + " requests");
        }
    }

    /// <summary>Reads the GC log from the start and returns the source keys of this job's import messages, in log order.</summary>
    public async Task<IReadOnlyList<string>> ReadSourceKeysAsync(string jobId, CancellationToken ct)
    {
        var keys = new List<string>();
        var after = 0L;
        while (true)
        {
            var page = new List<GcEvent>();
            await foreach (var ev in reader.ReadAsync(gcId, after, IGcLogReader.MaxLimit, viewer, ct))
            {
                page.Add(ev);
            }
            if (page.Count == 0)
            {
                break;
            }

            foreach (var ev in page)
            {
                after = ev.Seq;
                if (ev.Kind == GcEventKinds.Message && TryReadPayload(ev.PayloadJson, out var eventJob, out var sourceKey)
                    && string.Equals(eventJob, jobId, StringComparison.Ordinal))
                {
                    keys.Add(sourceKey);
                }
            }
        }
        return keys;
    }

    /// <summary>The idempotency key for a record: "import:" plus the SHA-256 of its source key (71 characters).</summary>
    public static string ClientMsgIdFor(string sourceKey)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceKey))).ToLowerInvariant();
        return "import:" + hash;
    }

    private static string PayloadFor(string jobId, int batchIndex, SourceRecord record) =>
        JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["import_job"] = jobId,
            ["import_batch"] = batchIndex.ToString(CultureInfo.InvariantCulture),
            ["source_key"] = record.SourceKey,
            ["kind"] = record.Kind,
            ["title"] = record.Title,
        });

    private static bool TryReadPayload(string? json, out string jobId, out string sourceKey)
    {
        jobId = string.Empty;
        sourceKey = string.Empty;
        if (string.IsNullOrEmpty(json))
        {
            return false;
        }

        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (values is null || !values.TryGetValue("import_job", out var job) || !values.TryGetValue("source_key", out var key))
            {
                return false;
            }
            jobId = job;
            sourceKey = key;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// Finds imported GC messages by kind and title, with the version each one is at now. Only messages written by the import
/// carry a title in their payload, so ordinary chat is not matched. A message's version is the highest edit seen in the log.
/// </summary>
public sealed class GcExistingObjectIndex(IGcLogReader reader, Guid gcId, GcPrincipal viewer) : IExistingObjectIndex
{
    public async Task<IReadOnlyList<ExistingObject>> FindAsync(IReadOnlyList<(string Kind, string Title)> wanted, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        if (wanted.Count == 0)
        {
            return [];
        }

        var stored = new Dictionary<Guid, (string Kind, string Title, Guid Author, int Version)>();
        var after = 0L;
        while (true)
        {
            var page = new List<GcEvent>();
            await foreach (var ev in reader.ReadAsync(gcId, after, IGcLogReader.MaxLimit, viewer, ct))
            {
                page.Add(ev);
            }
            if (page.Count == 0)
            {
                break;
            }

            foreach (var ev in page)
            {
                after = ev.Seq;
                if (ev.Kind == GcEventKinds.Message && ev.MessageId is { } id && TryReadStored(ev.PayloadJson, out var kind, out var title))
                {
                    stored.TryAdd(id, (kind, title, ev.AuthorId, 1));
                }
                else if (ev.Kind == GcEventKinds.MessageEdit && ev.MessageId is { } editOf && ev.Version is { } version
                    && stored.TryGetValue(editOf, out var current) && version > current.Version)
                {
                    stored[editOf] = current with { Version = version };
                }
            }
        }

        var found = new List<ExistingObject>();
        var seen = new HashSet<(string, string)>();
        foreach (var (id, item) in stored)
        {
            if (wanted.Contains((item.Kind, item.Title)) && seen.Add((item.Kind, item.Title)))
            {
                found.Add(new ExistingObject(item.Kind, item.Title, id.ToString("D"), item.Version.ToString(CultureInfo.InvariantCulture))
                {
                    Owner = item.Author.ToString("D"),
                });
            }
        }
        return found;
    }

    private static bool TryReadStored(string? json, out string kind, out string title)
    {
        kind = string.Empty;
        title = string.Empty;
        if (string.IsNullOrEmpty(json))
        {
            return false;
        }
        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (values is null || !values.ContainsKey("import_job") || !values.TryGetValue("kind", out var k) || !values.TryGetValue("title", out var t))
            {
                return false;
            }
            kind = k;
            title = t;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
