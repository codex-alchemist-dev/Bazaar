using System.Runtime.CompilerServices;
using System.Text;

namespace Bazaar.Portability.Tests;

public class MilanoteImporterTests
{
    private const string _markedExport = """
        {"items": [
          {"milanote": true, "type": "text", "title": "Intro", "text": "Hello board"},
          {"type": "list", "text": "Buy milk"},
          {"type": "link", "content": "https://example.com"}
        ]}
        """;

    private const string _unmarkedExport = """
        {"notes": [
          {"text": "First"},
          {"text": "Second"}
        ]}
        """;

    private static ImportSource Src(string name, string text) => new(name, Encoding.UTF8.GetBytes(text));

    /// <summary>Reads a fixture from the source tree next to this file, so no csproj copy rule is needed.</summary>
    private static ImportSource Fixture(string name, [CallerFilePath] string here = "") =>
        new(name, File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(here)!, "Fixtures", name)));

    [Fact]
    public void Detect_with_marker_returns_0_8()
    {
        var importer = new MilanoteImporter();
        Assert.Equal(0.8, importer.Detect(Src("board.json", _markedExport)).Confidence);
    }

    [Fact]
    public void Detect_with_name_marker_returns_0_8()
    {
        var importer = new MilanoteImporter();
        Assert.Equal(0.8, importer.Detect(Src("MILANOTE-board.json", _unmarkedExport)).Confidence);
    }

    [Fact]
    public void Detect_with_shape_only_returns_0_4()
    {
        var importer = new MilanoteImporter();
        Assert.Equal(0.4, importer.Detect(Src("board.json", _unmarkedExport)).Confidence);
    }

    [Fact]
    public void Detect_rejects_malformed_json()
    {
        var importer = new MilanoteImporter();
        Assert.Equal(0, importer.Detect(Src("milanote.json", "{\"items\": [")).Confidence);
    }

    [Fact]
    public void Detect_rejects_json_without_item_list()
    {
        var importer = new MilanoteImporter();
        Assert.Equal(0, importer.Detect(Src("milanote.json", "{\"a\": 1}")).Confidence);
    }

    [Fact]
    public void Parse_returns_one_record_per_item()
    {
        var importer = new MilanoteImporter();
        var records = importer.Parse(Src("board.json", _markedExport));

        Assert.Equal(3, records.Count);
        Assert.Equal(["text", "list", "link"], records.Select(r => r.Kind).ToArray());
        Assert.Equal("Intro", records[0].Title);
        Assert.Equal("Hello board", records[0].Body);
        Assert.Equal("Buy milk", records[1].Title);
        Assert.Equal("https://example.com", records[2].Body);
    }

    [Fact]
    public void Parse_defaults_kind_to_note_and_sets_attributes()
    {
        var importer = new MilanoteImporter();
        var records = importer.Parse(Src("board.json", _unmarkedExport));

        Assert.Equal(2, records.Count);
        var first = records[0];
        Assert.Equal("note", first.Kind);
        Assert.Equal("milanote", first.Attributes["format"]);
        Assert.Equal("board.json", first.Attributes["source"]);
        Assert.Equal("note", first.Attributes["type"]);
    }

    [Fact]
    public void Parse_gives_identical_items_distinct_source_keys()
    {
        var importer = new MilanoteImporter();
        var records = importer.Parse(Src("board.json", """{"items": [{"text": "same"}, {"text": "same"}]}"""));

        Assert.Equal(2, records.Count);
        Assert.NotEqual(records[0].SourceKey, records[1].SourceKey);
    }

    [Fact]
    public void Parse_truncates_title_to_80_characters_when_no_title()
    {
        var importer = new MilanoteImporter();
        var longText = new string('a', 100);
        var records = importer.Parse(Src("board.json", $"{{\"items\": [{{\"text\": \"{longText}\"}}]}}"));

        Assert.Equal(80, records[0].Title.Length);
        Assert.Equal(longText, records[0].Body);
    }

    [Fact]
    public void Parse_accepts_root_array()
    {
        var importer = new MilanoteImporter();
        var records = importer.Parse(Src("board.json", """[{"name": "Card", "content": "Body"}]"""));

        var record = Assert.Single(records);
        Assert.Equal("Card", record.Title);
        Assert.Equal("Body", record.Body);
    }

    [Fact]
    public void Parse_of_malformed_json_returns_nothing()
    {
        var importer = new MilanoteImporter();
        Assert.Empty(importer.Parse(Src("milanote.json", "not json")));
    }

    [Fact]
    public void Fixture_milanote_export_parses_count_titles_and_bodies()
    {
        var importer = new MilanoteImporter();
        var records = importer.Parse(Fixture("milanote-export.json"));

        Assert.Equal(4, records.Count);
        Assert.Equal(["Launch plan", "Write changelog", "Moodboard", "https://example.com/spec"], records.Select(r => r.Title).ToArray());
        Assert.Equal(["Ship the beta on Friday", "Write changelog", "Moodboard", "https://example.com/spec"], records.Select(r => r.Body).ToArray());
    }
}
