using System.Text;
using System.Text.Json;

namespace Bazaar.Server.Portability;

/// <summary>JSON Lines export: one object per record, UTF-8, LF line endings (doc 44 section 4.1).</summary>
public sealed class JsonLinesExporter : IPortabilityExporter
{
    public string Id => "bazaar.export.jsonl";
    public string Version => "1.0.0";
    public string FileExtension => ".jsonl";

    public byte[] Export(IReadOnlyList<SourceRecord> records)
    {
        var sb = new StringBuilder();
        foreach (var record in records)
        {
            sb.Append(JsonSerializer.Serialize(new
            {
                sourceKey = record.SourceKey,
                kind = record.Kind,
                title = record.Title,
                body = record.Body,
                attributes = record.Attributes,
            }));
            sb.Append('\n');
        }
        return new UTF8Encoding(false).GetBytes(sb.ToString());
    }
}

/// <summary>Markdown export: one document per record, with a front matter block carrying its source key.</summary>
public sealed class MarkdownExporter : IPortabilityExporter
{
    public string Id => "bazaar.export.md";
    public string Version => "1.0.0";
    public string FileExtension => ".md";

    public byte[] Export(IReadOnlyList<SourceRecord> records)
    {
        var sb = new StringBuilder();
        foreach (var record in records)
        {
            sb.Append("---\n");
            sb.Append("source_key: ").Append(record.SourceKey).Append('\n');
            sb.Append("kind: ").Append(record.Kind).Append('\n');
            sb.Append("---\n\n");
            sb.Append("# ").Append(record.Title).Append("\n\n");
            sb.Append(record.Body.Replace("\r\n", "\n"));
            if (!record.Body.EndsWith('\n')) sb.Append('\n');
            sb.Append('\n');
        }
        return new UTF8Encoding(false).GetBytes(sb.ToString());
    }
}
