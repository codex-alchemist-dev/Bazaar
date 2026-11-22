using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.Portability;

/// <summary>The audit export could not be produced or did not verify. The export fails closed: no files are returned.</summary>
public sealed class AuditExportException(string message) : Exception(message);

/// <summary>
/// File layout of the audit export (doc 44 section 4.6): <c>audit/entries.jsonl</c>, <c>audit/checkpoints.jsonl</c> and
/// <c>audit/archive.json</c>. UTF-8, LF, one JSON object per line, byte fields in Base64, snake_case names.
/// </summary>
public static class AuditArchiveFormat
{
    /// <summary>Path of the row file.</summary>
    public const string EntriesPath = "audit/entries.jsonl";

    /// <summary>Path of the checkpoint file.</summary>
    public const string CheckpointsPath = "audit/checkpoints.jsonl";

    /// <summary>Path of the archive marker.</summary>
    public const string ArchivePath = "audit/archive.json";

    /// <summary>Folder for inclusion-proof files.</summary>
    public const string ProofsFolder = "audit/proofs/";

    /// <summary>Format id inside <see cref="ArchivePath"/>.</summary>
    public const string ArchiveFormatId = "bazaar.audit.archive.v1";

    internal static byte[] WriteRows(IEnumerable<AuditRow> rows) => WriteLines(rows.Select(ToLine), AuditJson.Default.RowLine);

    internal static byte[] WriteCheckpoints(IEnumerable<AuditCheckpointRecord> checkpoints) =>
        WriteLines(checkpoints.Select(ToLine), AuditJson.Default.CheckpointLine);

    internal static byte[] WriteArchive(ArchiveMarker marker) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(marker, AuditJsonIndented.Default.ArchiveMarker) + "\n");

    /// <summary>Parses the row file. Returns null for malformed content; never throws on bad input.</summary>
    public static IReadOnlyList<AuditRow>? TryReadRows(ReadOnlySpan<byte> content)
    {
        var lines = TryReadLines(content, AuditJson.Default.RowLine);
        if (lines is null)
        {
            return null;
        }

        var rows = new List<AuditRow>(lines.Count);
        foreach (var line in lines)
        {
            if (line.Actor is null || line.Kind is null || line.Payload is null || line.PrevHash is null || line.Hash is null)
            {
                return null;
            }

            rows.Add(new AuditRow(line.Seq, line.Ts, line.Actor, line.Kind, line.Payload, line.PrevHash, line.Hash));
        }

        return rows;
    }

    /// <summary>
    /// Reads the row file one line at a time, so a large archive is never held in memory. A line that is not a valid row (or
    /// invalid UTF-8) yields one null and ends the sequence; <see cref="AuditChainReplay"/> reports a null row as malformed input.
    /// </summary>
    public static IEnumerable<AuditRow> StreamRows(Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var reader = new StreamReader(content, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        while (true)
        {
            string? line = null;
            var invalid = false;
            try
            {
                line = reader.ReadLine();
            }
            catch (DecoderFallbackException)
            {
                invalid = true;
            }

            if (invalid)
            {
                yield return null!;
                yield break;
            }

            if (line is null)
            {
                yield break;
            }

            if (line.Length == 0)
            {
                continue;
            }

            RowLine? parsed = null;
            try
            {
                parsed = JsonSerializer.Deserialize(line, AuditJson.Default.RowLine);
            }
            catch (JsonException)
            {
            }

            if (parsed is null || parsed.Actor is null || parsed.Kind is null || parsed.Payload is null || parsed.PrevHash is null || parsed.Hash is null)
            {
                yield return null!;
                yield break;
            }

            yield return new AuditRow(parsed.Seq, parsed.Ts, parsed.Actor, parsed.Kind, parsed.Payload, parsed.PrevHash, parsed.Hash);
        }
    }

    /// <summary>Parses the checkpoint file. Returns null for malformed content; never throws on bad input.</summary>
    public static IReadOnlyList<AuditCheckpointRecord>? TryReadCheckpoints(ReadOnlySpan<byte> content)
    {
        var lines = TryReadLines(content, AuditJson.Default.CheckpointLine);
        if (lines is null)
        {
            return null;
        }

        var checkpoints = new List<AuditCheckpointRecord>(lines.Count);
        foreach (var line in lines)
        {
            if (line.ChainHeadHash is null || line.MerkleRoot is null || line.KeyId is null || line.Signature is null)
            {
                return null;
            }

            checkpoints.Add(new AuditCheckpointRecord(
                line.FirstSeq, line.LastSeq, line.RowCount, line.ChainHeadHash, line.MerkleRoot, line.CreatedAt, line.KeyId, line.Signature));
        }

        return checkpoints;
    }

    /// <summary>Parses the archive marker. Returns null for malformed content.</summary>
    public static ArchiveMarker? TryReadArchive(ReadOnlySpan<byte> content)
    {
        try
        {
            return JsonSerializer.Deserialize(content, AuditJson.Default.ArchiveMarker);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static RowLine ToLine(AuditRow row) =>
        new(row.Seq, row.Ts.ToUniversalTime(), row.Actor, row.Kind, row.Payload, row.PrevHash, row.Hash);

    private static CheckpointLine ToLine(AuditCheckpointRecord c) =>
        new(c.FirstSeq, c.LastSeq, c.RowCount, c.ChainHeadHash, c.MerkleRoot, c.CreatedAt.ToUniversalTime(), c.KeyId, c.Signature);

    private static byte[] WriteLines<T>(IEnumerable<T> items, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        var sb = new StringBuilder();
        foreach (var item in items)
        {
            sb.Append(JsonSerializer.Serialize(item, info)).Append('\n');
        }

        return new UTF8Encoding(false).GetBytes(sb.ToString());
    }

    private static List<T>? TryReadLines<T>(ReadOnlySpan<byte> content, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
        where T : class
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(content);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var result = new List<T>();
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                var item = JsonSerializer.Deserialize(line, info);
                if (item is null)
                {
                    return null;
                }

                result.Add(item);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return result;
    }
}

internal sealed record RowLine(long Seq, DateTimeOffset Ts, string? Actor, string? Kind, string? Payload, byte[]? PrevHash, byte[]? Hash);

internal sealed record CheckpointLine(
    long FirstSeq,
    long LastSeq,
    long RowCount,
    byte[]? ChainHeadHash,
    byte[]? MerkleRoot,
    DateTimeOffset CreatedAt,
    string? KeyId,
    byte[]? Signature);

/// <summary>
/// The marker file that records, inside the bundle manifest's file list, that this is an archive and not part of the live
/// chain (doc 44 section 8.2). It carries no clock value, so the export stays deterministic.
/// </summary>
public sealed record ArchiveMarker(
    string? Format,
    bool PartOfLiveChain,
    string? ImportRule,
    long? StartCheckpointLastSeq,
    long FirstRowSeq,
    long LastRowSeq,
    long RowCount,
    long CheckpointCount,
    long UncheckpointedTailRows);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(RowLine))]
[JsonSerializable(typeof(CheckpointLine))]
[JsonSerializable(typeof(ArchiveMarker))]
[JsonSerializable(typeof(InclusionProofFile))]
internal sealed partial class AuditJson : JsonSerializerContext;

/// <summary>The same shapes, indented, for the human-readable single-object files.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ArchiveMarker))]
[JsonSerializable(typeof(InclusionProofFile))]
internal sealed partial class AuditJsonIndented : JsonSerializerContext;
