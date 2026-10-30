using Bazaar.Graph.Extraction;
using Bazaar.Graph.Model;

namespace Bazaar.Graph.Tests;

public sealed class MarkdownExtractorTests
{
    private const string _doc = """
        # Guide
        Intro with [[Other Note]] and [[Ref|alias]].

        ## Setup
        See [the API](../api/Auth.md) and [site](https://example.com).

        ```
        # not a heading [[Ignored]]
        ```

        ### Details
        Back to top.
        """;

    private static FileFragment Extract() => MarkdownExtractor.Extract("docs/guide/Guide.md", "hash", _doc);

    [Fact]
    public void Builds_a_heading_tree_and_skips_fenced_code()
    {
        var fragment = Extract();
        var sections = fragment.Nodes.Where(n => n.Kind == NodeKind.Section).ToList();

        Assert.Equal(3, sections.Count);
        Assert.Contains(fragment.Edges, e => e.Source == "docs/guide/Guide.md" && e.Target == "docs/guide/Guide.md#guide");
        Assert.Contains(fragment.Edges, e => e.Source == "docs/guide/Guide.md#guide" && e.Target == "docs/guide/Guide.md#setup");
        Assert.Contains(fragment.Edges, e => e.Source == "docs/guide/Guide.md#setup" && e.Target == "docs/guide/Guide.md#details");
        Assert.DoesNotContain(fragment.Refs, r => r.Name == "Ignored");
    }

    [Fact]
    public void Records_wiki_links_and_relative_path_links()
    {
        var fragment = Extract();

        Assert.Contains(fragment.Refs, r => r.Name == "Other Note" && !r.ByPath && r.Relation == Relation.Links);
        Assert.Contains(fragment.Refs, r => r.Name == "Ref" && !r.ByPath);
        Assert.Contains(fragment.Refs, r => r.Name == "../api/Auth.md" && r.ByPath && r.Source == "docs/guide/Guide.md#setup");
        Assert.DoesNotContain(fragment.Refs, r => r.Name.Contains("example.com", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Setup Details", "setup-details")]
    [InlineData("What's new?", "whats-new")]
    public void Slugs_match_github_style_anchors(string title, string expected) =>
        Assert.Equal(expected, MarkdownExtractor.Slug(title));
}
