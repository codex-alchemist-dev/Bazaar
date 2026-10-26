using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Bazaar.Server.Vault.Markdown;

/// <summary>Parses Vault Markdown: front matter plus wiki-links with absolute source spans. Owned by VLT1 (wiki-link parser).</summary>
public static class VaultMarkdown
{
    // Markdig pipelines are immutable once built and the wiki-link parser keeps no per-parse state, so one instance is shared.
    private static readonly MarkdownPipeline Pipeline = Build();

    /// <summary>
    /// Finds every wiki-link in <paramref name="source"/>, in document order. The YAML front matter block is not inline-parsed,
    /// so links inside it are not reported. <see cref="VaultDocument.FrontMatter"/> is left null here.
    /// </summary>
    public static VaultDocument Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var document = Markdig.Markdown.Parse(source, Pipeline);
        var links = document.Descendants<WikiLinkInline>()
            .Select(inline => inline.Link)
            .OrderBy(link => link.Start)
            .ToList();
        return new VaultDocument(source, null, links);
    }

    private static MarkdownPipeline Build()
    {
        // Precise locations let the parser map inline offsets back to the source, including inside blockquotes and lists.
        var builder = new MarkdownPipelineBuilder().UseYamlFrontMatter().UsePreciseSourceLocation();
        builder.InlineParsers.Insert(0, new WikiLinkInlineParser());
        return builder.Build();
    }
}
