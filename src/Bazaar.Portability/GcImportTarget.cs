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
            if (item.Replaces is not null)
            {
                // Replacing a GC message is not implemented yet. Refuse the batch rather than append a copy the audit calls a replace.
                throw new NotSupportedException("Replace is not implemented for the GC target; the batch is refused.");
            }
            var body = await codec.SealAsync(authorId, item.Record.Body, ct);
            requests.Add(new AppendRequest(gcId, GcEventKinds.Message, authorKind, authorId, SourceTrust.External, ClientMsgIdFor(item.Provenance.SourceKey))
            {
                Message = body,
                PayloadJson = PayloadFor(jobId, batchIndex, item.Provenance.SourceKey),
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

    private static string PayloadFor(string jobId, int batchIndex, string sourceKey) =>
        JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["import_job"] = jobId,
            ["import_batch"] = batchIndex.ToString(CultureInfo.InvariantCulture),
            ["source_key"] = sourceKey,
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
