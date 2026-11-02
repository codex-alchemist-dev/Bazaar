using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;

namespace Bazaar.Server.Vault.Markdown;

/// <summary>
/// One top-level block of a Vault document (doc 18, <c>vault_blocks</c>). <see cref="Start"/> and <see cref="Length"/> are
/// absolute offsets into the source; <see cref="StartLine"/> and <see cref="EndLine"/> are 1-based and inclusive.
/// <see cref="HeadingPath"/> lists the headings enclosing the block, outermost first; a heading block includes itself.
/// </summary>
public sealed record VaultBlock(
    int Ordinal,
    string? BlockId,
    IReadOnlyList<string> HeadingPath,
    int StartLine,
    int EndLine,
    int Start,
    int Length,
    byte[] TextHash,
    string Body);

/// <summary>
/// Splits a Vault document into the blocks the index stores. Each top-level Markdown block is one block, so a paragraph,
/// list, table, code fence or heading is addressable. The YAML front matter is not a block. A block id is a trailing
/// <c>^id</c> on the block's last line (Obsidian form).
/// </summary>
public static class VaultBlocks
{
    private static readonly Regex _blockIdPattern = new(@"(?:^|\s)\^([A-Za-z0-9-]+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<VaultBlock> Split(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var document = Markdig.Markdown.Parse(source, VaultMarkdown.Pipeline);
        var lineStarts = LineStarts(source);
        var blocks = new List<VaultBlock>();
        var headings = new List<(int Level, string Text)>();

        foreach (var block in document)
        {
            if (block is YamlFrontMatterBlock)
            {
                continue;
            }

            var start = block.Span.Start;
            var length = block.Span.Length;
            var body = source.Substring(start, length).TrimEnd('\r', '\n');

            if (block is HeadingBlock heading)
            {
                var text = HeadingText(body);
                headings.RemoveAll(entry => entry.Level >= heading.Level);
                headings.Add((heading.Level, text));
            }

            var lastLine = body.Split('\n')[^1].TrimEnd('\r');
            var idMatch = _blockIdPattern.Match(lastLine);
            var blockId = idMatch.Success ? idMatch.Groups[1].Value : null;

            blocks.Add(new VaultBlock(
                blocks.Count,
                blockId,
                headings.Select(entry => entry.Text).ToArray(),
                LineOf(lineStarts, start),
                LineOf(lineStarts, start + Math.Max(length - 1, 0)),
                start,
                length,
                SHA256.HashData(Encoding.UTF8.GetBytes(body)),
                body));
        }

        return blocks;
    }

    /// <summary>The ordinal of the block that contains <paramref name="offset"/>, or -1 when none does (front matter).</summary>
    public static int OrdinalAt(IReadOnlyList<VaultBlock> blocks, int offset)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            if (offset >= blocks[i].Start && offset < blocks[i].Start + blocks[i].Length)
            {
                return blocks[i].Ordinal;
            }
        }

        return -1;
    }

    private static string HeadingText(string body)
    {
        var firstLine = body.Split('\n')[0].Trim();
        var text = firstLine.TrimStart('#').Trim();
        return text.TrimEnd('#').Trim();
    }

    private static List<int> LineStarts(string source)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    private static int LineOf(List<int> lineStarts, int offset)
    {
        var index = lineStarts.BinarySearch(offset);
        return (index >= 0 ? index : ~index - 1) + 1;
    }
}
