using System.Text;

namespace Bazaar.Portability.Tests;

// The CSV header names and the fixture are modelled, not verified against a real Miro export.
public class MiroImporterTests
{
    private static ImportSource Src(string name, string text) => new(name, Encoding.UTF8.GetBytes(text));

    /// <summary>Reads a fixture copied next to the test binary (see the csproj Fixtures item).</summary>
    private static ImportSource Fixture(string name) =>
        new(name, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));

    [Fact]
    public void Csv_with_text_column_and_miro_name_is_detected_at_0_8()
    {
        var importer = new MiroImporter();
        Assert.Equal(0.8, importer.Detect(Src("miro-board.csv", "Type,Text\nsticky_note,hi\n")).Confidence);
    }

    [Fact]
    public void Csv_with_text_column_without_miro_name_is_detected_at_0_5()
    {
        var importer = new MiroImporter();
        Assert.Equal(0.5, importer.Detect(Src("board.csv", "Type,Text\nsticky_note,hi\n")).Confidence);
    }

    [Fact]
    public void Csv_without_text_or_title_column_is_not_detected()
    {
        var importer = new MiroImporter();
        Assert.Equal(0, importer.Detect(Src("miro.csv", "a,b\n1,2\n")).Confidence);
    }

    [Fact]
    public void Empty_csv_is_not_detected()
    {
        var importer = new MiroImporter();
        Assert.Equal(0, importer.Detect(Src("miro.csv", "")).Confidence);
    }

    [Fact]
    public void Non_csv_files_are_not_detected()
    {
        var importer = new MiroImporter();
        Assert.Equal(0, importer.Detect(Src("miro-export.json", "{\"data\":[]}")).Confidence);
        Assert.Equal(0, importer.Detect(Src("miro.xlsx", "Type,Text\n")).Confidence);
    }

    [Fact]
    public void Each_data_row_becomes_one_record()
    {
        var importer = new MiroImporter();
        var records = importer.Parse(Src("board.csv", "Type,Title,Text\nsticky_note,First,Alpha\ntext,Second,Beta\n"));

        Assert.Equal(["First", "Second"], records.Select(r => r.Title).ToArray());
        Assert.Equal(["Alpha", "Beta"], records.Select(r => r.Body).ToArray());
        Assert.Equal(["sticky_note", "text"], records.Select(r => r.Kind).ToArray());
    }

    [Fact]
    public void Quoted_fields_keep_commas_line_breaks_and_doubled_quotes()
    {
        var importer = new MiroImporter();
        var record = Assert.Single(importer.Parse(Src("board.csv", "Text\n\"a, \"\"b\"\"\nc\"\n")));

        Assert.Equal("a, \"b\"\nc", record.Body);
    }

    [Fact]
    public void Crlf_line_endings_are_handled()
    {
        var importer = new MiroImporter();
        var records = importer.Parse(Src("board.csv", "Title,Text\r\nOne,First\r\nTwo,Second\r\n"));

        Assert.Equal(["One", "Two"], records.Select(r => r.Title).ToArray());
        Assert.Equal(["First", "Second"], records.Select(r => r.Body).ToArray());
    }

    [Fact]
    public void Header_names_match_ignoring_case_and_spaces()
    {
        var importer = new MiroImporter();
        var record = Assert.Single(importer.Parse(Src("board.csv", " TYPE , Content \nframe,Hello\n")));

        Assert.Equal("frame", record.Kind);
        Assert.Equal("Hello", record.Body);
    }

    [Fact]
    public void Rows_without_text_are_skipped()
    {
        var importer = new MiroImporter();
        var record = Assert.Single(importer.Parse(Src("board.csv", "Type,Text\nsticky_note,\nshape,   \nsticky_note,Keep me\n")));

        Assert.Equal("Keep me", record.Body);
    }

    [Fact]
    public void Title_falls_back_to_the_first_80_characters_of_text()
    {
        var importer = new MiroImporter();
        var text = new string('x', 100);
        var record = Assert.Single(importer.Parse(Src("board.csv", $"Text\n{text}\n")));

        Assert.Equal(80, record.Title.Length);
        Assert.Equal(text, record.Body);
    }

    [Fact]
    public void Missing_type_column_gives_kind_item()
    {
        var importer = new MiroImporter();
        var record = Assert.Single(importer.Parse(Src("board.csv", "Text\nhello\n")));

        Assert.Equal("item", record.Kind);
        Assert.Equal("item", record.Attributes["type"]);
    }

    [Fact]
    public void Stray_quote_inside_unquoted_field_is_kept()
    {
        var importer = new MiroImporter();
        var record = Assert.Single(importer.Parse(Src("board.csv", "Text\nsay \"hi\"\n")));

        Assert.Equal("say \"hi\"", record.Body);
    }

    [Fact]
    public void Attributes_and_source_key_are_set()
    {
        var importer = new MiroImporter();
        var record = Assert.Single(importer.Parse(Src("board.csv", "Type,Text\nsticky_note,First\n")));

        Assert.Equal("miro", record.Attributes["format"]);
        Assert.Equal("board.csv", record.Attributes["source"]);
        Assert.Equal("sticky_note", record.Attributes["type"]);
        Assert.Equal(SourceKeys.For("board.csv#1", "First"), record.SourceKey);
    }

    [Fact]
    public void Header_only_csv_parses_to_nothing()
    {
        var importer = new MiroImporter();
        Assert.Empty(importer.Parse(Src("board.csv", "Type,Title,Text\n")));
    }

    [Fact]
    public void Importer_identity_is_stable()
    {
        var importer = new MiroImporter();
        Assert.Equal("bazaar.import.miro", importer.Id);
        Assert.Equal("1.0.0", importer.Version);
    }

    [Fact]
    public void Fixture_miro_export_parses_kinds_titles_and_bodies()
    {
        var importer = new MiroImporter();
        var records = importer.Parse(Fixture("miro-export.csv"));

        Assert.Equal(3, records.Count);
        Assert.Equal(["sticky_note", "text", "frame"], records.Select(r => r.Kind).ToArray());
        Assert.Equal(["Ship beta", "Retro: say \"thanks\" more", "Sprint 12"], records.Select(r => r.Title).ToArray());
        Assert.Equal(
            ["Ship the beta on Friday, after the signing check", "Retro: say \"thanks\" more", "Sprint 12 planning"],
            records.Select(r => r.Body).ToArray());
    }
}
