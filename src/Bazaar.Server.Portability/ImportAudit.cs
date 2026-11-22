using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Portability;

/// <summary>
/// One audit entry per committed batch (doc 44 section 8.1, stage 9). <see cref="BatchNumber"/> is 1-based;
/// <see cref="ContentHash"/> is the SHA-256 of the batch content (see <see cref="ImportBatchHash"/>).
/// </summary>
public sealed record ImportAuditRecord(
    string Kind,
    string JobId,
    int BatchNumber,
    int ItemCount,
    string ContentHash,
    DateTimeOffset At,
    string? SourceKey = null,
    string? ReplacedObjectId = null,
    string? ReplacedVersion = null,
    string? Detail = null)
{
    /// <summary>The audit kind written for each batch.</summary>
    public const string BatchKind = "portability.import.batch";

    /// <summary>The audit kind written for each owner decision on a quarantined record (doc 44 section 8.5). Detail holds the choice and rule ids, never the secret.</summary>
    public const string SecretDecisionKind = "portability.import.secret_decision";

    /// <summary>The audit kind written for each owner replace (doc 44 section 8.10), with the replaced object's version.</summary>
    public const string ReplaceKind = "portability.import.replace";
}

/// <summary>Sink for import audit entries. The host wires it to the audit log (doc 15).</summary>
public interface IAuditSink
{
    /// <summary>Appends one entry. Throws when the entry cannot be stored.</summary>
    Task AppendAsync(ImportAuditRecord record, CancellationToken ct);
}

/// <summary>SHA-256 over a batch: for each item, in order, the source key, a NUL, the body, and a newline.</summary>
public static class ImportBatchHash
{
    public static string Of(IReadOnlyList<ImportItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var builder = new StringBuilder();
        foreach (var item in items)
        {
            builder.Append(item.Record.SourceKey).Append('\0').Append(item.Record.Body).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}
