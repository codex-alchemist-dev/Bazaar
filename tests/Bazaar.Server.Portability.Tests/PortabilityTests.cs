using System.Text;

namespace Bazaar.Server.Portability.Tests;

public class PortabilityTests
{
    private static ImportSource Src(string name, string text) => new(name, Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Plain_text_detects_and_parses_one_document()
    {
        var importer = new PlainTextImporter();
        var source = Src("notes.txt", "line one\r\nline two");
        Assert.True(importer.Detect(source).Confidence > 0);
        var records = importer.Parse(source);
        Assert.Single(records);
        Assert.Equal("notes", records[0].Title);
        Assert.Equal("line one\nline two", records[0].Body);
    }

    [Fact]
    public void Binary_content_is_rejected_by_plain_text_detector()
    {
        var importer = new PlainTextImporter();
        var source = new ImportSource("blob.bin", [0x50, 0x4B, 0x00, 0x03]);
        Assert.Equal(0, importer.Detect(source).Confidence);
    }

    [Fact]
    public void Invalid_utf8_is_rejected()
    {
        var importer = new PlainTextImporter();
        var source = new ImportSource("bad.txt", [0xC3, 0x28]);
        Assert.Equal(0, importer.Detect(source).Confidence);
    }

    [Fact]
    public void Markdown_reads_front_matter_and_heading_title()
    {
        var importer = new MarkdownImporter();
        var source = Src("ignored.md", "---\nauthor: Dom\n---\n# Roadmap\n\nBody text\n");
        var record = Assert.Single(importer.Parse(source));
        Assert.Equal("Roadmap", record.Title);
        Assert.Equal("Dom", record.Attributes["frontmatter.author"]);
        Assert.StartsWith("# Roadmap", record.Body);
    }

    [Fact]
    public void Source_key_is_stable_for_same_source_and_content()
    {
        var importer = new MarkdownImporter();
        var a = importer.Parse(Src("a.md", "# A\n"))[0].SourceKey;
        var b = importer.Parse(Src("a.md", "# A\n"))[0].SourceKey;
        var c = importer.Parse(Src("a.md", "# B\n"))[0].SourceKey;
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Reimport_is_a_no_op_by_source_key()
    {
        var importer = new PlainTextImporter();
        var parsed = importer.Parse(Src("n.txt", "hello"));
        var first = ImportPlanner.Plan(parsed, new HashSet<string>());
        Assert.Single(first.New);
        var committed = first.New.Select(r => r.SourceKey).ToHashSet();
        var second = ImportPlanner.Plan(importer.Parse(Src("n.txt", "hello")), committed);
        Assert.Empty(second.New);
        Assert.Single(second.Duplicates);
    }

    [Fact]
    public void Markdown_export_round_trips_title_and_body()
    {
        var record = new SourceRecord("k1", "document", "Plan", "Step one\n", new Dictionary<string, string>());
        var text = Encoding.UTF8.GetString(new MarkdownExporter().Export([record]));
        Assert.Contains("source_key: k1", text);
        var reread = new MarkdownImporter().Parse(new ImportSource("plan.md", Encoding.UTF8.GetBytes(text)))[0];
        Assert.Equal("Plan", reread.Title);
        Assert.Contains("Step one", reread.Body);
    }

    [Fact]
    public void Jsonl_export_writes_one_line_per_record_with_lf()
    {
        var records = new[]
        {
            new SourceRecord("k1", "document", "A", "x", new Dictionary<string, string>()),
            new SourceRecord("k2", "document", "B", "y", new Dictionary<string, string>()),
        };
        var text = Encoding.UTF8.GetString(new JsonLinesExporter().Export(records));
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.DoesNotContain('\r', text);
        Assert.Contains("\"sourceKey\":\"k2\"", lines[1]);
    }
}
