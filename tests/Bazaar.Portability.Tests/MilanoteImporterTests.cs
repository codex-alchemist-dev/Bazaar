using System.Text;

namespace Bazaar.Portability.Tests;

// The Markdown layout and the fixture are modelled, not verified against a real Milanote export.
public class MilanoteImporterTests
{
    private static ImportSource Src(string name, string text) => new(name, Encoding.UTF8.GetBytes(text));

    /// <summary>Reads a fixture copied next to the test binary (see the csproj Fixtures item).</summary>
    private static ImportSource Fixture(string name) =>
        new(name, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));

    [Fact]
    public void Detect_with_name_marker_returns_0_8()
    {
        var importer = new MilanoteImporter();
        Assert.Equal(0.8, importer.Detect(Src("milanote-board.md", "plain text")).Confidence);
    }

    [Fact]
    public void Detect_with_headings_only_returns_0_3()
    {
        var importer = new MilanoteImporter();
        Assert.Equal(0.3, importer.Detect(Src("board.md", "# Card\n\nBody")).Confidence);
    }

    [Fact]
    public void Detect_rejects_markdown_without_headings()
    {
        var importer = new MilanoteImporter();
        Assert.Equal(0, importer.Detect(Src("notes.md", "just prose, no headings")).Confidence);
    }

    [Fact]
    public void Detect_rejects_other_file_types()
    {
        var importer = new MilanoteImporter();
        Assert.Equal(0, importer.Detect(Src("milanote.json", "# Card")).Confidence);
        Assert.Equal(0, importer.Detect(Src("milanote.zip", "# Card")).Confidence);
    }

    [Fact]
    public void Parse_makes_one_card_per_heading()
    {
        var importer = new MilanoteImporter();
        var records = importer.Parse(Src("board.md", "# Intro\nHello board\n\n## List\n- Buy milk\n"));

        Assert.Equal(["Intro", "List"], records.Select(r => r.Title).ToArray());
        Assert.Equal("Hello board", records[0].Body);
        Assert.Equal("- Buy milk", records[1].Body);
        Assert.Equal(["note", "note"], records.Select(r => r.Kind).ToArray());
    }

    [Fact]
    public void Text_before_first_heading_becomes_a_card_titled_by_its_start()
    {
        var importer = new MilanoteImporter();
        var record = Assert.Single(importer.Parse(Src("board.md", "Loose note at the top\n")));

        Assert.Equal("Loose note at the top", record.Title);
        Assert.Equal("Loose note at the top", record.Body);
    }

    [Fact]
    public void Heading_needs_a_space_and_at_most_six_hashes()
    {
        var importer = new MilanoteImporter();
        var records = importer.Parse(Src("board.md", "#NoSpace\n####### Seven\n# Real\nBody"));

        // The two non-headings stay in the preamble card; only "# Real" starts a card.
        Assert.Equal(2, records.Count);
        Assert.Equal("#NoSpace\n####### Seven", records[0].Body);
        Assert.Equal("Real", records[1].Title);
        Assert.Equal("Body", records[1].Body);
    }

    [Fact]
    public void Empty_heading_card_is_skipped()
    {
        var importer = new MilanoteImporter();
        Assert.Empty(importer.Parse(Src("board.md", "# \n\n")));
    }

    [Fact]
    public void Crlf_line_endings_are_handled()
    {
        var importer = new MilanoteImporter();
        var records = importer.Parse(Src("board.md", "# One\r\nFirst\r\n# Two\r\nSecond\r\n"));

        Assert.Equal(["One", "Two"], records.Select(r => r.Title).ToArray());
        Assert.Equal("First", records[0].Body);
        Assert.Equal("Second", records[1].Body);
    }

    [Fact]
    public void Attributes_are_set()
    {
        var importer = new MilanoteImporter();
        var record = Assert.Single(importer.Parse(Src("board.md", "# Card\nBody")));

        Assert.Equal("milanote", record.Attributes["format"]);
        Assert.Equal("board.md", record.Attributes["source"]);
        Assert.Equal("note", record.Attributes["type"]);
    }

    [Fact]
    public void Identical_cards_get_distinct_source_keys()
    {
        var importer = new MilanoteImporter();
        var records = importer.Parse(Src("board.md", "# Same\nbody\n# Same\nbody\n"));

        Assert.Equal(2, records.Count);
        Assert.NotEqual(records[0].SourceKey, records[1].SourceKey);
    }

    [Fact]
    public void Long_card_text_is_truncated_to_80_characters_for_the_title()
    {
        var importer = new MilanoteImporter();
        var longText = new string('a', 100);
        var record = Assert.Single(importer.Parse(Src("board.md", longText)));

        Assert.Equal(80, record.Title.Length);
        Assert.Equal(longText, record.Body);
    }

    [Fact]
    public void Fixture_milanote_export_parses_titles_and_bodies()
    {
        var importer = new MilanoteImporter();
        var records = importer.Parse(Fixture("milanote-export.md"));

        Assert.Equal(["Launch plan", "Write changelog", "Moodboard"], records.Select(r => r.Title).ToArray());
        Assert.Equal(
            ["Ship the beta on Friday.", "Summarise the portability work.", "https://example.com/spec"],
            records.Select(r => r.Body).ToArray());
    }
}
