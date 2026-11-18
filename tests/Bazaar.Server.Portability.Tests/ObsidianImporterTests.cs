using System.Text;

namespace Bazaar.Server.Portability.Tests;

public class ObsidianImporterTests
{
    private static ImportSource Src(string name, string text) => new(name, Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Front_matter_title_wins_over_heading()
    {
        var importer = new ObsidianImporter();
        var source = Src("plan.md", "---\ntitle: Dom's Plan\n---\n# Heading\n\nBody [[Link]]\n");
        var record = Assert.Single(importer.Parse(source));
        Assert.Equal("Dom's Plan", record.Title);
        Assert.Equal("obsidian", record.Attributes["format"]);
        Assert.Equal("plan.md", record.Attributes["source"]);
    }

    [Fact]
    public void Wiki_links_are_preserved_byte_for_byte_in_body()
    {
        var importer = new ObsidianImporter();
        const string body = "See [[Some Note]] and [[Other|shown as]] and ![[diagram.png]].\n";
        var record = Assert.Single(importer.Parse(Src("n.md", body)));
        Assert.Equal(body, record.Body);
    }

    [Fact]
    public void Alias_and_heading_links_are_extracted_to_targets()
    {
        var importer = new ObsidianImporter();
        var source = Src(
            "links.md",
            "# Links\n[[Target|Alias]] [[Page#Section]] [[Page#Other|x]] ![[diagram.png]]\n" +
            "#project/alpha and #todo\n## Heading #notatag\n```\n#insidefence\n```\n");
        var record = Assert.Single(importer.Parse(source));
        Assert.Equal("Target,Page,diagram.png", record.Attributes["links"]);
        Assert.Equal("project/alpha,todo", record.Attributes["tags"]);
    }

    [Fact]
    public void Plain_note_without_links_detects_as_markdown_at_0_7()
    {
        var importer = new ObsidianImporter();
        var source = Src("plain.md", "Just some words.\n");
        Assert.Equal(0.7, importer.Detect(source).Confidence);
        var record = Assert.Single(importer.Parse(source));
        Assert.Equal("", record.Attributes["links"]);
        Assert.Equal("plain", record.Title);
    }

    [Fact]
    public void Note_with_wiki_links_detects_at_0_9()
    {
        var importer = new ObsidianImporter();
        Assert.Equal(0.9, importer.Detect(Src("any.txt", "A [[link]] here")).Confidence);
    }
}
