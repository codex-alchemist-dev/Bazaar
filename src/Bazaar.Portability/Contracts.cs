using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Portability;

/// <summary>A raw source handed to an importer. Nothing is interpreted before a detector runs.</summary>
public sealed record ImportSource(string Name, byte[] Content);

/// <summary>One object read from a source. Parsing never writes to the instance.</summary>
public sealed record SourceRecord(
    string SourceKey,
    string Kind,
    string Title,
    string Body,
    IReadOnlyDictionary<string, string> Attributes);

/// <summary>Detection result: confidence in [0,1] and a short reason shown in the preview.</summary>
public readonly record struct DetectResult(double Confidence, string Reason);

/// <summary>
/// Importer module contract. Importers are modules (doc 44 D3): they carry an id and version and
/// are selected by detection, never by file extension alone.
/// </summary>
public interface IPortabilityImporter
{
    string Id { get; }
    string Version { get; }
    DetectResult Detect(ImportSource source);
    IReadOnlyList<SourceRecord> Parse(ImportSource source);
}

/// <summary>Exporter module contract. Exports are reads; they never change the source data.</summary>
public interface IPortabilityExporter
{
    string Id { get; }
    string Version { get; }
    string FileExtension { get; }
    byte[] Export(IReadOnlyList<SourceRecord> records);
}

/// <summary>Stable source keys make re-importing the same source a no-op.</summary>
public static class SourceKeys
{
    public static string For(string sourceName, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(sourceName + "\0" + content);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}

/// <summary>Result of previewing an import against the keys already committed.</summary>
public sealed record ImportPreview(
    IReadOnlyList<SourceRecord> New,
    IReadOnlyList<SourceRecord> Duplicates)
{
    /// <summary>
    /// Source keys of new records whose owner choice is replace (doc 44 section 8.10). The committer must write
    /// each one as <c>portability.import.replace</c> in the batch audit, with the replaced object's version.
    /// </summary>
    public IReadOnlyList<string> ReplaceSourceKeys { get; init; } = [];
}

public static class ImportPlanner
{
    /// <summary>Splits parsed records into new and already-imported (by source key). Pure.</summary>
    public static ImportPreview Plan(IEnumerable<SourceRecord> parsed, IReadOnlySet<string> committedKeys)
    {
        var seen = new HashSet<string>(committedKeys, StringComparer.Ordinal);
        var fresh = new List<SourceRecord>();
        var dupes = new List<SourceRecord>();
        foreach (var record in parsed)
        {
            if (seen.Add(record.SourceKey)) fresh.Add(record);
            else dupes.Add(record);
        }
        return new ImportPreview(fresh, dupes);
    }
}
