using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Portability;

/// <summary>Status values of an import dry-run. Strings so they can be stored and shown as-is.</summary>
public static class ImportStatus
{
    public const string Ready = "ready";
    public const string Ambiguous = "ambiguous";
    public const string Unrecognised = "unrecognised";
    public const string Failed = "failed";
}

/// <summary>One importer that offered to handle a source, with its confidence and reason.</summary>
public sealed record ImportCandidate(string Id, double Confidence, string Reason);

/// <summary>
/// Outcome of a staged import run. <see cref="MappingHash"/> is empty unless the status is ready.
/// <see cref="Message"/> explains failed and unrecognised outcomes.
/// </summary>
public sealed record ImportPipelineResult(
    string Status,
    string? ImporterId,
    IReadOnlyList<ImportCandidate> Candidates,
    ImportPreview? Preview,
    string MappingHash,
    string? Message = null);

/// <summary>
/// Staged, in-memory import pipeline (doc 44 section 8.1): detect, parse, plan and hash.
/// Pure: no database, no vault writes, no network. Never throws for importer failures.
/// </summary>
public sealed class ImportPipeline
{
    /// <summary>Two candidates whose confidence differs by at most this amount need the owner to choose.</summary>
    public const double AmbiguityMargin = 0.1;

    // Floating-point slack so that a difference of exactly 0.1 counts as "within 0.1".
    private const double _marginEpsilon = 1e-9;

    private readonly List<IPortabilityImporter> _importers;
    private readonly ImportLimits _limits;

    public ImportPipeline(IEnumerable<IPortabilityImporter> importers) : this(importers, ImportLimits.Default)
    {
    }

    public ImportPipeline(IEnumerable<IPortabilityImporter> importers, ImportLimits limits)
    {
        ArgumentNullException.ThrowIfNull(importers);
        ArgumentNullException.ThrowIfNull(limits);
        _importers = [.. importers];
        _limits = limits;
    }

    public ImportPipelineResult Run(ImportSource source, IReadOnlySet<string> committedKeys, string? preferredImporterId = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(committedKeys);

        // Checked before any importer sees the bytes (doc 44 sections 8.3 and 12, max_upload_bytes).
        if (source.Content.LongLength > _limits.MaxSourceBytes)
        {
            return Failed(null, [], $"source is {source.Content.LongLength} bytes, over the limit of {_limits.MaxSourceBytes}");
        }

        var detected = new List<(IPortabilityImporter Importer, ImportCandidate Candidate)>();
        try
        {
            foreach (var importer in _importers)
            {
                var result = importer.Detect(source);
                if (result.Confidence <= 0) continue;
                detected.Add((importer, new ImportCandidate(importer.Id, result.Confidence, result.Reason)));
            }
        }
        catch (Exception ex)
        {
            return Failed(null, [], "detect failed: " + ex.Message);
        }

        detected.Sort((a, b) =>
        {
            var byConfidence = b.Candidate.Confidence.CompareTo(a.Candidate.Confidence);
            return byConfidence != 0 ? byConfidence : string.CompareOrdinal(a.Candidate.Id, b.Candidate.Id);
        });
        var candidates = detected.Select(d => d.Candidate).ToList();

        IPortabilityImporter chosen;
        if (preferredImporterId is not null)
        {
            var preferred = _importers.FirstOrDefault(i => string.Equals(i.Id, preferredImporterId, StringComparison.Ordinal));
            if (preferred is null)
            {
                return Unrecognised(candidates, "preferred importer is not registered: " + preferredImporterId);
            }
            chosen = preferred;
        }
        else
        {
            if (detected.Count == 0)
            {
                return Unrecognised(candidates, "no importer recognised this source");
            }
            if (detected.Count > 1 && detected[0].Candidate.Confidence - detected[1].Candidate.Confidence <= AmbiguityMargin + _marginEpsilon)
            {
                return new ImportPipelineResult(ImportStatus.Ambiguous, null, candidates, null, string.Empty);
            }
            chosen = detected[0].Importer;
        }

        try
        {
            var parsed = chosen.Parse(source);
            foreach (var record in parsed)
            {
                var bytes = Encoding.UTF8.GetByteCount(record.Body);
                if (bytes > _limits.MaxRecordBytes)
                {
                    return Failed(chosen.Id, candidates, $"record '{record.Title}' is {bytes} bytes, over the limit of {_limits.MaxRecordBytes}");
                }
            }
            var preview = ImportPlanner.Plan(parsed, committedKeys);
            var hash = MappingHashOf(chosen.Id, chosen.Version, preview.New);
            return new ImportPipelineResult(ImportStatus.Ready, chosen.Id, candidates, preview, hash);
        }
        catch (Exception ex)
        {
            return Failed(chosen.Id, candidates, ex.Message);
        }
    }

    /// <summary>Commit is allowed only against a ready dry-run whose mapping hash matches the current mapping.</summary>
    public static bool CanCommit(ImportPipelineResult dryRun, string currentMappingHash)
    {
        ArgumentNullException.ThrowIfNull(dryRun);
        ArgumentNullException.ThrowIfNull(currentMappingHash);
        return dryRun.Status == ImportStatus.Ready
            && dryRun.MappingHash.Length > 0
            && string.Equals(dryRun.MappingHash, currentMappingHash, StringComparison.Ordinal);
    }

    private static ImportPipelineResult Unrecognised(IReadOnlyList<ImportCandidate> candidates, string message) =>
        new(ImportStatus.Unrecognised, null, candidates, null, string.Empty, message);

    private static ImportPipelineResult Failed(string? importerId, IReadOnlyList<ImportCandidate> candidates, string message) =>
        new(ImportStatus.Failed, importerId, candidates, null, string.Empty, message);

    /// <summary>SHA-256 hex over the importer id, its version and the new source keys in order.</summary>
    private static string MappingHashOf(string importerId, string version, IReadOnlyList<SourceRecord> newRecords)
    {
        var builder = new StringBuilder();
        builder.Append(importerId).Append('\0').Append(version);
        foreach (var record in newRecords)
        {
            builder.Append('\0').Append(record.SourceKey);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}
