using Bazaar.Server.Vault.Markdown;

namespace Bazaar.Server.Tests.Vault.Markdown;

/// <summary>Inline cases that the golden files do not cover directly: container offsets, embeds and the non-link fallbacks.</summary>
public class WikiLinkParserTests
{
    [Fact]
    public void Offsets_are_absolute_inside_blockquotes_list_items_and_wrapped_paragraphs()
    {
        const string source = "> quote [[a]]\n> more\n\n- item [[b]]\n\t[[tabbed]]\n\nwrapped\n[[c]] end";

        var links = VaultMarkdown.Parse(source).Links;

        Assert.Equal(["a", "b", "tabbed", "c"], links.Select(link => link.Target));
        foreach (var link in links)
        {
            Assert.Equal($"[[{link.Target}]]", source.Substring(link.Start, link.Length));
        }
    }

    [Fact]
    public void Tabs_before_and_inside_a_line_keep_the_span_exact()
    {
        const string source = "- item\tfirst [[x]]\n  more\t[[y|tab\talias]]\n";

        var links = VaultMarkdown.Parse(source).Links;

        Assert.Equal(2, links.Count);
        Assert.Equal("[[x]]", source.Substring(links[0].Start, links[0].Length));
        Assert.Equal("[[y|tab\talias]]", source.Substring(links[1].Start, links[1].Length));
    }

    [Fact]
    public void Embed_span_starts_at_the_bang()
    {
        const string source = "text ![[pic|caption]] after";

        var link = Assert.Single(VaultMarkdown.Parse(source).Links);

        Assert.Equal(WikiLinkKind.Embed, link.Kind);
        Assert.Equal("![[pic|caption]]", source.Substring(link.Start, link.Length));
        Assert.Equal("caption", link.Alias);
    }

    [Fact]
    public void Link_inside_markdown_link_text_is_reported_and_the_markdown_link_still_parses()
    {
        const string source = "[see [[x]]](https://example.com) and [plain](https://example.com/y)";

        var document = VaultMarkdown.Parse(source);

        Assert.Equal(["x"], document.Links.Select(link => link.Target));
        Assert.Equal("[[x]]", source.Substring(document.Links[0].Start, document.Links[0].Length));
    }

    [Fact]
    public void Standard_markdown_links_and_references_are_not_wiki_links()
    {
        const string source = "[text](https://example.com) and [text][ref] and ![alt](img.png)\n\n[ref]: https://example.com/ref";

        Assert.Empty(VaultMarkdown.Parse(source).Links);
    }

    [Theory]
    [InlineData("[[]]")]
    [InlineData("[[ ]]")]
    [InlineData("[[|only alias]]")]
    [InlineData("[[#]]")]
    [InlineData("[[a[b]]")]
    [InlineData("[[a]b]]")]
    [InlineData("[[unclosed")]
    [InlineData("\\[[x]]")]
    [InlineData("`[[x]]`")]
    [InlineData("```\n[[x]]\n```")]
    public void Forms_that_are_not_links_produce_no_links(string source)
    {
        Assert.Empty(VaultMarkdown.Parse(source).Links);
    }

    [Fact]
    public void Heading_anchor_with_hash_inside_keeps_the_rest_as_text()
    {
        const string source = "[[Auth#Token#flow]]";

        var link = Assert.Single(VaultMarkdown.Parse(source).Links);

        Assert.Equal("Auth", link.Target);
        Assert.Equal("Token#flow", link.Anchor);
    }
}
