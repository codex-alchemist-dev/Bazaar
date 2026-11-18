using System.Text;

namespace Bazaar.Server.Portability;

/// <summary>Plain text: one document per source. Title is the file name without extension.</summary>
public sealed class PlainTextImporter : IPortabilityImporter
{
    public string Id => "bazaar.import.txt";
    public string Version => "1.0.0";

    public DetectResult Detect(ImportSource source)
    {
        if (source.Content.Length == 0) return new DetectResult(0, "empty");
        // Valid UTF-8 without NUL bytes is treated as text.
        if (Array.IndexOf(source.Content, (byte)0) >= 0) return new DetectResult(0, "binary (NUL bytes)");
        try
        {
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(source.Content);
        }
        catch (DecoderFallbackException)
        {
            return new DetectResult(0, "not valid UTF-8");
        }
        return new DetectResult(0.3, "plain text");
    }

    public IReadOnlyList<SourceRecord> Parse(ImportSource source)
    {
        var text = Encoding.UTF8.GetString(source.Content).Replace("\r\n", "\n");
        return [new SourceRecord(
            SourceKeys.For(source.Name, text),
            "document",
            Path.GetFileNameWithoutExtension(source.Name),
            text,
            new Dictionary<string, string> { ["format"] = "txt", ["source"] = source.Name })];
    }
}

/// <summary>Markdown: title from the first level-one heading, else the file name. YAML front matter is kept as an attribute block.</summary>
public sealed class MarkdownImporter : IPortabilityImporter
{
    public string Id => "bazaar.import.md";
    public string Version => "1.0.0";

    public DetectResult Detect(ImportSource source)
    {
        var name = source.Name.ToLowerInvariant();
        if (name.EndsWith(".md", StringComparison.Ordinal) || name.EndsWith(".markdown", StringComparison.Ordinal))
            return new DetectResult(0.9, "markdown extension");
        var head = Encoding.UTF8.GetString(source.Content.AsSpan(0, Math.Min(source.Content.Length, 512)));
        if (head.Contains("\n# ", StringComparison.Ordinal) || head.StartsWith("# ", StringComparison.Ordinal))
            return new DetectResult(0.5, "markdown heading");
        return new DetectResult(0, "no markdown markers");
    }

    public IReadOnlyList<SourceRecord> Parse(ImportSource source)
    {
        var text = Encoding.UTF8.GetString(source.Content).Replace("\r\n", "\n");
        var attributes = new Dictionary<string, string> { ["format"] = "md", ["source"] = source.Name };
        var body = text;
        if (text.StartsWith("---\n", StringComparison.Ordinal))
        {
            var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (end > 0)
            {
                foreach (var line in text.Substring(4, end - 4).Split('\n'))
                {
                    var colon = line.IndexOf(':');
                    if (colon > 0) attributes[$"frontmatter.{line[..colon].Trim()}"] = line[(colon + 1)..].Trim();
                }
                body = text[(end + 4)..].TrimStart('\n');
            }
        }
        var title = Path.GetFileNameWithoutExtension(source.Name);
        foreach (var line in body.Split('\n'))
        {
            if (line.StartsWith("# ", StringComparison.Ordinal)) { title = line[2..].Trim(); break; }
        }
        return [new SourceRecord(SourceKeys.For(source.Name, text), "document", title, body, attributes)];
    }
}
