using System.Text.RegularExpressions;
using Bazaar.Graph.Model;

namespace Bazaar.Graph.Extraction;

/// <summary>Extracts headings (as a section tree), <c>[[wiki-links]]</c> and relative <c>[text](path)</c> links from one Markdown file.</summary>
public static partial class MarkdownExtractor
{
    public static FileFragment Extract(string relativePath, string sha256, string text)
    {
        var builder = new FragmentBuilder(relativePath, sha256);
        builder.AddFileNode();

        // Stack of (level, id) for the heading tree: a heading is contained by the nearest shallower heading.
        var stack = new List<(int Level, string Id)>();
        var inFence = false;
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                continue;
            }

            var heading = HeadingRegex().Match(line);
            if (heading.Success)
            {
                var level = heading.Groups[1].Length;
                var title = heading.Groups[2].Value.Trim();
                while (stack.Count > 0 && stack[^1].Level >= level)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                var parent = stack.Count > 0 ? stack[^1].Id : builder.FileId;
                var id = builder.AddNode(NodeKind.Section, title, relativePath, i + 1, relativePath + "#" + Slug(title));
                builder.AddEdge(parent, id, Relation.Contains, Confidence.Extracted);
                stack.Add((level, id));
            }

            var source = stack.Count > 0 ? stack[^1].Id : builder.FileId;
            foreach (Match wiki in WikiLinkRegex().Matches(line))
            {
                builder.AddRef(source, Relation.Links, wiki.Groups[1].Value.Trim(), byPath: false);
            }

            foreach (Match link in MarkdownLinkRegex().Matches(line))
            {
                var target = link.Groups[1].Value.Trim();
                if (IsRelativeFilePath(target))
                {
                    builder.AddRef(source, Relation.Links, Uri.UnescapeDataString(target.Split('#')[0]), byPath: true);
                }
            }
        }

        return builder.Build();
    }

    /// <summary>GitHub-style anchor: lowercase, spaces to hyphens, punctuation dropped.</summary>
    public static string Slug(string title)
    {
        var chars = title.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : c is ' ' or '-' ? '-' : '\0')
            .Where(c => c != '\0');
        return new string(chars.ToArray());
    }

    private static bool IsRelativeFilePath(string target) =>
        target.Length > 0
        && !target.StartsWith('#')
        && !target.Contains("://", StringComparison.Ordinal)
        && !target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^(#{1,6})\s+(.+?)\s*#*\s*$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"\[\[([^\]|#]+)(?:[#|][^\]]*)?\]\]")]
    private static partial Regex WikiLinkRegex();

    [GeneratedRegex(@"(?<!!)\[[^\]]*\]\(([^)\s]+)(?:\s+""[^""]*"")?\)")]
    private static partial Regex MarkdownLinkRegex();
}
