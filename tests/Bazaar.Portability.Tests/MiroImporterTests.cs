using System.Text;

namespace Bazaar.Portability.Tests;

public class MiroImporterTests
{
    private static ImportSource Src(string name, string json) => new(name, Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Shape_with_known_item_type_is_detected_at_half_confidence()
    {
        var importer = new MiroImporter();
        var source = Src("board.json", """{"data":[{"type":"sticky_note","data":{"content":"hi"}}]}""");
        var result = importer.Detect(source);
        Assert.Equal(0.5, result.Confidence);
    }

    [Fact]
    public void Miro_file_name_is_detected_at_high_confidence()
    {
        var importer = new MiroImporter();
        var source = Src("miro-export.json", """{"data":[{"type":"other","data":"x"}]}""");
        Assert.Equal(0.8, importer.Detect(source).Confidence);
    }

    [Fact]
    public void Frames_only_root_is_not_detected()
    {
        var importer = new MiroImporter();
        var source = Src("miro.json", """{"frames":["Sprint"]}""");
        Assert.Equal(0, importer.Detect(source).Confidence);
    }

    [Fact]
    public void Non_miro_json_is_not_detected()
    {
        var importer = new MiroImporter();
        Assert.Equal(0, importer.Detect(Src("config.json", """{"name":"x"}""")).Confidence);
        Assert.Equal(0, importer.Detect(Src("board.json", """{"data":[{"type":"other"}]}""")).Confidence);
        Assert.Equal(0, importer.Detect(Src("data.json", """[{"type":"sticky_note"}]""")).Confidence);
    }

    [Fact]
    public void Invalid_json_is_not_detected_even_with_miro_name()
    {
        var importer = new MiroImporter();
        var result = importer.Detect(Src("miro-board.json", "{\"data\": [ "));
        Assert.Equal(0, result.Confidence);
    }

    [Fact]
    public void Html_tags_are_stripped_from_body_and_title()
    {
        var importer = new MiroImporter();
        var source = Src("board.json", """{"data":[{"type":"text","data":{"content":"<p>Hello <b>world</b></p>"}}]}""");
        var record = Assert.Single(importer.Parse(source));
        Assert.Equal("Hello world", record.Body);
        Assert.Equal("Hello world", record.Title);
        Assert.Equal("text", record.Kind);
    }

    [Fact]
    public void Less_than_without_closing_bracket_is_kept()
    {
        var importer = new MiroImporter();
        var source = Src("board.json", """{"data":[{"type":"text","data":"a < b"}]}""");
        var record = Assert.Single(importer.Parse(source));
        Assert.Equal("a < b", record.Body);
    }

    [Fact]
    public void Items_with_empty_or_missing_text_are_skipped()
    {
        var importer = new MiroImporter();
        var source = Src("board.json", """
            {"data":[
              {"type":"sticky_note","data":{"content":""}},
              {"type":"sticky_note","data":{"content":"<p></p>"}},
              {"type":"shape"},
              {"type":"sticky_note","data":{"content":"Keep me"}}
            ]}
            """);
        var record = Assert.Single(importer.Parse(source));
        Assert.Equal("Keep me", record.Body);
    }

    [Fact]
    public void Source_key_and_attributes_are_set()
    {
        var importer = new MiroImporter();
        var source = Src("board.json", """{"data":[{"type":"sticky_note","data":"First"}]}""");
        var record = Assert.Single(importer.Parse(source));
        Assert.Equal(SourceKeys.For("board.json#0", "First"), record.SourceKey);
        Assert.Equal("miro", record.Attributes["format"]);
        Assert.Equal("board.json", record.Attributes["source"]);
        Assert.Equal("sticky_note", record.Attributes["type"]);
    }

    [Fact]
    public void Title_is_capped_at_80_characters()
    {
        var importer = new MiroImporter();
        var text = new string('x', 100);
        var source = Src("board.json", $$"""{"data":[{"type":"text","data":"{{text}}"}]}""");
        var record = Assert.Single(importer.Parse(source));
        Assert.Equal(80, record.Title.Length);
        Assert.Equal(100, record.Body.Length);
    }

    [Fact]
    public void Frames_array_yields_frame_titles()
    {
        var importer = new MiroImporter();
        var source = Src("board.json", """{"data":[{"type":"text","data":"Note"}],"frames":["Sprint", {"title":"Retro"}]}""");
        var records = importer.Parse(source);
        Assert.Equal(3, records.Count);
        Assert.Equal("Sprint", records[1].Body);
        Assert.Equal("frame", records[1].Kind);
        Assert.Equal("Retro", records[2].Body);
    }

    [Fact]
    public void Invalid_json_parses_to_nothing()
    {
        var importer = new MiroImporter();
        Assert.Empty(importer.Parse(Src("miro.json", "not json")));
    }

    [Fact]
    public void Importer_identity_is_stable()
    {
        var importer = new MiroImporter();
        Assert.Equal("bazaar.import.miro", importer.Id);
        Assert.Equal("1.0.0", importer.Version);
    }
}
