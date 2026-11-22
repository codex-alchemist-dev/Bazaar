using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Portability;

/// <summary>The install setting <c>portability.import.secret_scan</c> (doc 44 section 12).</summary>
public enum SecretScanPolicy
{
    /// <summary>A record with a finding is held back until the owner decides. The default.</summary>
    Quarantine,

    /// <summary>Findings are reported in the preview, and records are not held back.</summary>
    Warn,

    /// <summary>No scan. Not allowed for text from an external source.</summary>
    Off,
}

/// <summary>What the owner decides for a quarantined record (doc 44 section 8.5).</summary>
public enum QuarantineChoice
{
    ImportAsIs,
    RedactAndImport,
    Skip,
}

/// <summary>The policy and whether the text comes from outside the instance. Off is refused for external text.</summary>
public sealed record SecretScanOptions
{
    private SecretScanOptions(SecretScanPolicy policy)
    {
        Policy = policy;
    }

    public SecretScanPolicy Policy { get; }

    /// <summary>Options for text from an external source (every normal import). <see cref="SecretScanPolicy.Off"/> is refused.</summary>
    public static SecretScanOptions ForExternal(SecretScanPolicy policy)
    {
        if (policy == SecretScanPolicy.Off)
            throw new ArgumentException("The secret scan cannot be turned off for text from an external source.", nameof(policy));
        return new SecretScanOptions(policy);
    }

    /// <summary>Options for a bundle from the same instance, where the host has verified the signature. Any policy is allowed.</summary>
    public static SecretScanOptions ForTrusted(SecretScanPolicy policy) => new(policy);
}

/// <summary>A record held back by the scan, with where each finding is.</summary>
public sealed record QuarantinedRecord(SourceRecord Record, IReadOnlyList<SecretFinding> Findings);

/// <summary>The scan outcome for a preview. <see cref="Flagged"/> lists records with findings under both Quarantine and Warn.</summary>
public sealed record SecretScanReport(SecretScanPolicy Policy, IReadOnlyList<QuarantinedRecord> Flagged)
{
    /// <summary>True when records are held back until the owner decides.</summary>
    public bool HoldsRecords => Policy == SecretScanPolicy.Quarantine && Flagged.Count > 0;
}

/// <summary>
/// Applies the secret-scan policy to an import preview (doc 44 section 8.5). Pure: the same preview and decisions
/// always give the same result. Both calls take the ORIGINAL preview, so the owner's decisions are a function of it.
/// </summary>
public static class ImportSecretGate
{
    /// <summary>Scans the new records. Under Off nothing is scanned.</summary>
    public static SecretScanReport Scan(ImportPreview preview, SecretScanOptions options)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(options);

        if (options.Policy == SecretScanPolicy.Off) return new SecretScanReport(options.Policy, []);

        var flagged = new List<QuarantinedRecord>();
        foreach (var record in preview.New)
        {
            var findings = ImportSecretScanner.Scan(record);
            if (findings.Count > 0) flagged.Add(new QuarantinedRecord(record, findings));
        }
        return new SecretScanReport(options.Policy, flagged);
    }

    /// <summary>
    /// Builds the preview that may be committed. Under Warn and Off it is the input preview. Under Quarantine a flagged
    /// record is kept only with a decision: as is, redacted (marked with a <c>secret_scan</c> attribute), or skipped.
    /// A flagged record with no decision stays held back. The result is a new preview, so it gets a new mapping hash
    /// and needs a new dry-run before commit.
    /// </summary>
    public static ImportPreview Resolve(
        ImportPreview preview,
        SecretScanOptions options,
        IReadOnlyDictionary<string, QuarantineChoice> decisions)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(decisions);

        if (options.Policy != SecretScanPolicy.Quarantine) return preview;

        var report = Scan(preview, options);
        var flaggedKeys = report.Flagged.Select(f => f.Record.SourceKey).ToHashSet(StringComparer.Ordinal);

        var kept = new List<SourceRecord>();
        foreach (var record in preview.New)
        {
            if (!flaggedKeys.Contains(record.SourceKey))
            {
                kept.Add(record);
                continue;
            }
            if (!decisions.TryGetValue(record.SourceKey, out var choice)) continue;
            switch (choice)
            {
                case QuarantineChoice.ImportAsIs:
                    kept.Add(Marked(record, record, "as_is"));
                    break;
                case QuarantineChoice.RedactAndImport:
                    kept.Add(Marked(record, record with
                    {
                        Title = ImportSecretScanner.Redact(record.Title),
                        Body = ImportSecretScanner.Redact(record.Body),
                    }, "redacted"));
                    break;
                case QuarantineChoice.Skip:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(decisions), choice, "Unknown quarantine choice.");
            }
        }

        var remaining = kept.Select(r => r.SourceKey).ToHashSet(StringComparer.Ordinal);
        return preview with
        {
            New = kept,
            ReplaceSourceKeys = [.. preview.ReplaceSourceKeys.Where(remaining.Contains)],
            ExistingCollisions = [.. preview.ExistingCollisions.Where(c => remaining.Contains(c.Incoming.SourceKey))],
        };
    }

    private static SourceRecord Marked(SourceRecord original, SourceRecord record, string marker)
    {
        var attributes = new Dictionary<string, string>(original.Attributes.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal)
        {
            ["secret_scan"] = marker,
        };
        return record with { Attributes = attributes };
    }
}

/// <summary>Writes one audit entry per owner decision on a quarantined record (doc 44 section 8.5).</summary>
public static class ImportSecretAudit
{
    public static async Task RecordDecisionsAsync(
        IAuditSink sink,
        string jobId,
        SecretScanReport report,
        IReadOnlyDictionary<string, QuarantineChoice> decisions,
        DateTimeOffset at,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(decisions);

        foreach (var flagged in report.Flagged)
        {
            if (!decisions.TryGetValue(flagged.Record.SourceKey, out var choice)) continue;

            var rules = string.Join(',', flagged.Findings.Select(f => f.RuleId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
            var detail = "choice=" + ChoiceName(choice) + ";rules=" + rules;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(flagged.Record.SourceKey + "\0" + detail))).ToLowerInvariant();
            await sink.AppendAsync(
                new ImportAuditRecord(ImportAuditRecord.SecretDecisionKind, jobId, 0, 1, hash, at, flagged.Record.SourceKey, Detail: detail),
                ct).ConfigureAwait(false);
        }
    }

    private static string ChoiceName(QuarantineChoice choice) => choice switch
    {
        QuarantineChoice.ImportAsIs => "import_as_is",
        QuarantineChoice.RedactAndImport => "redact_and_import",
        QuarantineChoice.Skip => "skip",
        _ => throw new ArgumentOutOfRangeException(nameof(choice), choice, "Unknown quarantine choice."),
    };
}
