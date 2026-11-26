using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Gc.Write;

namespace Bazaar.Server.Portability.Withdrawal;

/// <summary>
/// GC side of a withdrawal (doc 44 section 8.8, PROPOSED). For each imported message it appends a <c>message_edit</c> by the
/// system principal that carries <see cref="AppendRequest.WithdrawnByImport"/>, through <see cref="IGcLogWriter"/>, never by SQL.
/// The new version holds the same sealed body as the current one (the tombstone and body CHECK needs a body), so nothing is
/// decrypted or re-sealed here. It mirrors <c>MessageEditPlanner.PlanWithdrawal</c>, which lives in Bazaar.Server and so cannot
/// be called from this project.
/// </summary>
/// <remarks>
/// Idempotent: the idempotency key is derived from the job and the message, so a repeat is answered by the writer with the
/// original event and writes nothing, even if the message was edited in between. A message whose current version is a tombstone
/// (erased) has no body to carry and is skipped.
/// </remarks>
public sealed class GcWithdrawalMarker(
    IGcLogWriter writer,
    IGcLogReader reader,
    IGcMessageVersionSource versions,
    Guid gcId,
    GcPrincipal viewer) : IGcWithdrawalMarker
{
    /// <summary>The system principal's author id (the same value <c>MessageEditPlanner.SystemAuthorId</c> uses).</summary>
    public static readonly Guid SystemAuthorId = Guid.Empty;

    private const int _pageSize = 5_000;

    /// <inheritdoc />
    public async Task MarkWithdrawnAsync(string jobId, int batchIndex, IReadOnlyList<string> sourceKeys, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentNullException.ThrowIfNull(sourceKeys);
        if (jobId.Length > AppendRequest.MaxWithdrawnByImportLength)
        {
            throw new ArgumentException("Job id is too long to carry on a withdrawal version.", nameof(jobId));
        }
        if (sourceKeys.Count == 0)
        {
            return;
        }

        var wanted = new HashSet<string>(sourceKeys, StringComparer.Ordinal);
        var messageIds = await FindMessageIdsAsync(jobId, wanted, ct);
        var missing = wanted.Where(k => !messageIds.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException("No imported GC message found for source keys: " + string.Join(", ", missing));
        }

        var current = await ReadCurrentVersionsAsync(new HashSet<Guid>(messageIds.Values), ct);
        var rows = new List<AppendRequest>(messageIds.Count);
        foreach (var key in wanted.Order(StringComparer.Ordinal))
        {
            var messageId = messageIds[key];
            if (!current.TryGetValue(messageId, out var version))
            {
                throw new InvalidOperationException("No stored version found for message " + messageId.ToString("D"));
            }
            if (version.Tombstone || version.Ciphertext is null || version.KeyId is null)
            {
                continue;
            }

            rows.Add(new AppendRequest(gcId, GcEventKinds.MessageEdit, AuthorKind.System, SystemAuthorId, SourceTrust.System, ClientMsgIdFor(jobId, messageId))
            {
                MessageId = messageId,
                Version = version.Version + 1,
                Message = new MessageBody(version.Ciphertext, version.KeyId.Value, version.Nonce ?? []),
                WithdrawnByImport = jobId,
            });
        }

        if (rows.Count > 0)
        {
            await writer.AppendManyAsync(rows, ct);
        }
    }

    /// <summary>The idempotency key of one withdrawal: "withdraw:" plus the SHA-256 of the job id and message id.</summary>
    public static string ClientMsgIdFor(string jobId, Guid messageId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(jobId + "\0" + messageId.ToString("D")))).ToLowerInvariant();
        return "withdraw:" + hash;
    }

    private async Task<Dictionary<string, Guid>> FindMessageIdsAsync(string jobId, HashSet<string> wanted, CancellationToken ct)
    {
        var found = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var after = 0L;
        while (found.Count < wanted.Count)
        {
            var count = 0;
            await foreach (var ev in reader.ReadAsync(gcId, after, IGcLogReader.MaxLimit, viewer, ct))
            {
                count++;
                after = ev.Seq;
                if (ev.Kind == GcEventKinds.Message && ev.MessageId is { } id
                    && TryReadPayload(ev.PayloadJson, out var eventJob, out var sourceKey)
                    && string.Equals(eventJob, jobId, StringComparison.Ordinal) && wanted.Contains(sourceKey))
                {
                    found.TryAdd(sourceKey, id);
                }
            }
            if (count == 0)
            {
                break;
            }
        }
        return found;
    }

    private async Task<Dictionary<Guid, GcMessageVersionRecord>> ReadCurrentVersionsAsync(HashSet<Guid> messageIds, CancellationToken ct)
    {
        var latest = new Dictionary<Guid, GcMessageVersionRecord>();
        var after = 0L;
        while (true)
        {
            var count = 0;
            await foreach (var v in versions.ReadAsync(gcId, after, _pageSize, viewer, ct))
            {
                count++;
                after = v.Seq;
                if (messageIds.Contains(v.MessageId) && (!latest.TryGetValue(v.MessageId, out var seen) || v.Version > seen.Version))
                {
                    latest[v.MessageId] = v;
                }
            }
            if (count < _pageSize)
            {
                break;
            }
        }
        return latest;
    }

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
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("import_job", out var job) || job.ValueKind != JsonValueKind.String
                || !doc.RootElement.TryGetProperty("source_key", out var key) || key.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            jobId = job.GetString()!;
            sourceKey = key.GetString()!;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
