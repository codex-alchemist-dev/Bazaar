using System.Text;
using System.Text.RegularExpressions;

namespace Bazaar.Server.Portability;

/// <summary>
/// Obsidian note: one note per source. Wiki-links stay byte for byte in the body (Vault round-trip rule);
/// link targets and body tags are recorded as attributes. Front matter is split the same way as MarkdownImporter.
/// </summary>
public sealed class ObsidianImporter : IPortabilityImporter
{
    // Matches [[...]] and ![[...]] (embeds). The inner text is the link, alias and heading part.
    private static readonly Regex _wikiLink = new(@"\[\[([^\[\]\r\n]+?)\]\]", RegexOptions.CultureInvariant);
    private static readonly Regex _tagWord = new(@"(?:^|(?<=\s))#([\p{L}\p{N}_/-]+)", RegexOptions.CultureInvariant);

    public string Id => "bazaar.import.obsidian";
    public string Version => "1.0.0";

    public DetectResult Detect(ImportSource source)
    {
        var text = Encoding.UTF8.GetString(source.Content);
        if (_wikiLink.IsMatch(text)) return new DetectResult(0.9, "wiki-links");
        if (source.Name.ToLowerInvariant().EndsWith(".md", StringComparison.Ordinal))
            return new DetectResult(0.7, "markdown extension");
        return new DetectResult(0, "no wiki-links");
    }

    public IReadOnlyList<SourceRecord> Parse(ImportSource source)
    {
        var text = Encoding.UTF8.GetString(source.Content).Replace("\r\n", "\n", StringComparison.Ordinal);
        var (frontMatter, body) = SplitFrontMatter(text);

        var links = new List<string>();
        foreach (Match match in _wikiLink.Matches(body))
        {
            var target = LinkTarget(match.Groups[1].Value);
            if (target.Length > 0 && !links.Contains(target, StringComparer.Ordinal)) links.Add(target);
        }

        var tags = new List<string>();
        foreach (var line in ProseLines(body))
        {
            if (IsHeading(line)) continue;
            foreach (Match match in _tagWord.Matches(line))
            {
                var tag = match.Groups[1].Value;
                // Obsidian tags need at least one non-numeric character, so #1984 is not a tag.
                if (tag.Any(c => !char.IsDigit(c)) && !tags.Contains(tag, StringComparer.Ordinal)) tags.Add(tag);
            }
        }

        var attributes = new Dictionary<string, string>
        {
            ["format"] = "obsidian",
            ["source"] = source.Name,
            ["links"] = string.Join(',', links),
            ["tags"] = string.Join(',', tags),
        };
        var title = TitleFrom(frontMatter, body) ?? Path.GetFileNameWithoutExtension(source.Name);
        return [new SourceRecord(SourceKeys.For(source.Name, text), "document", title, body, attributes)];
    }

    /// <summary>Same front matter rule as MarkdownImporter: a leading "---" block of "key: value" lines.</summary>
    private static (Dictionary<string, string> FrontMatter, string Body) SplitFrontMatter(string text)
    {
        var frontMatter = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!text.StartsWith("---\n", StringComparison.Ordinal)) return (frontMatter, text);
        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end <= 0) return (frontMatter, text);
        foreach (var line in text.Substring(4, end - 4).Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) frontMatter[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return (frontMatter, text[(end + 4)..].TrimStart('\n'));
    }

    /// <summary>Front matter title, else the first level-one heading outside code fences; null if neither.</summary>
    private static string? TitleFrom(Dictionary<string, string> frontMatter, string body)
    {
        if (frontMatter.TryGetValue("title", out var raw) && raw.Length > 0) return Unquote(raw);
        foreach (var line in ProseLines(body))
        {
            if (!IsHeading(line) || !line.StartsWith("# ", StringComparison.Ordinal)) continue;
            var heading = line[2..].Trim();
            if (heading.Length > 0) return heading;
        }
        return null;
    }

    /// <summary>Target of [[target|alias]] or [[target#heading]]: the part before '|' and before '#'.</summary>
    private static string LinkTarget(string inner)
    {
        var target = inner;
        var pipe = target.IndexOf('|');
        if (pipe >= 0) target = target[..pipe];
        var hash = target.IndexOf('#');
        if (hash >= 0) target = target[..hash];
        return target.Trim();
    }

    /// <summary>Body lines outside fenced code blocks (``` or ~~~).</summary>
    private static List<string> ProseLines(string body)
    {
        var lines = new List<string>();
        var inFence = false;
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (!inFence) lines.Add(line);
        }
        return lines;
    }

    /// <summary>ATX heading: one to six '#' followed by a space or nothing.</summary>
    private static bool IsHeading(string line)
    {
        var hashes = 0;
        while (hashes < line.Length && line[hashes] == '#') hashes++;
        return hashes is >= 1 and <= 6 && (hashes == line.Length || line[hashes] == ' ');
    }

    private static string Unquote(string value)
    {
        var quoted = value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''));
        return quoted ? value[1..^1] : value;
    }
}
