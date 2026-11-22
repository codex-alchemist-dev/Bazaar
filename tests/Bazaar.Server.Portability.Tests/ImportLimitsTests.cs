using System.IO.Compression;
using System.Text;

namespace Bazaar.Server.Portability.Tests;

// Doc 44 section 8.3 parser limits and archive safety rules, and the section 12 settings they come from.
public class ImportLimitsTests
{
    private const string _wordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static byte[] MakeZip(Action<ZipArchive> fill)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            fill(zip);
        }
        return buffer.ToArray();
    }

    private static ZipArchiveEntry Write(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
        return entry;
    }

    private static string DocumentXml(string body) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"" + _wordNamespace + "\"><w:body>" + body + "</w:body></w:document>";

    private static byte[] MakeDocx(string documentXml, Action<ZipArchive>? extra = null) =>
        MakeZip(zip =>
        {
            Write(zip, "[Content_Types].xml", "<Types/>");
            Write(zip, "word/document.xml", documentXml);
            extra?.Invoke(zip);
        });

    [Fact]
    public void Defaults_follow_the_doc_44_settings()
    {
        var limits = ImportLimits.Default;

        Assert.Equal(10L * 1024 * 1024 * 1024, limits.MaxSourceBytes);
        Assert.Equal(100, limits.MaxExpansionRatio);
        Assert.Equal(10, ImportLimits.MinExpansionRatio);
    }

    [Fact]
    public void The_expansion_ratio_cannot_go_below_its_floor()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImportLimits(maxExpansionRatio: 9));
        Assert.Equal(10, new ImportLimits(maxExpansionRatio: 10).MaxExpansionRatio);
    }

    [Fact]
    public void Non_positive_limits_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImportLimits(maxSourceBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImportLimits(maxArchiveEntries: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImportLimits(maxNestingDepth: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImportLimits(maxRecordBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImportLimits(maxExpandedBytes: 0));
    }

    [Fact]
    public void A_compression_bomb_is_refused_before_it_is_expanded_in_full()
    {
        // 3 MiB of one letter deflates to a few KB, a ratio near 1000:1.
        var bomb = MakeDocx(DocumentXml("<w:p><w:r><w:t>" + new string('a', 3 * 1024 * 1024) + "</w:t></w:r></w:p>"));
        var importer = new DocxImporter();

        var error = Assert.Throws<ImportLimitException>(() => importer.Parse(new ImportSource("bomb.docx", bomb)));

        Assert.Contains("expands past the limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_expanded_byte_cap_stops_a_part_that_is_too_large()
    {
        var docx = MakeDocx(DocumentXml("<w:p><w:r><w:t>" + new string('a', 4096) + "</w:t></w:r></w:p>"));
        var importer = new DocxImporter(new ImportLimits(maxExpandedBytes: 1024));

        Assert.Throws<ImportLimitException>(() => importer.Parse(new ImportSource("big.docx", docx)));
    }

    [Fact]
    public void A_normal_document_parses_under_the_default_limits()
    {
        var docx = MakeDocx(DocumentXml("<w:p><w:r><w:t>Hello</w:t></w:r></w:p><w:p><w:r><w:t>World</w:t></w:r></w:p>"));

        var record = Assert.Single(new DocxImporter().Parse(new ImportSource("ok.docx", docx)));

        Assert.Equal("Hello\nWorld", record.Body);
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("\\windows\\system32\\evil.dll")]
    [InlineData("C:/temp/evil.txt")]
    [InlineData("word/../../evil.txt")]
    [InlineData("..\\evil.txt")]
    public void Unsafe_entry_paths_are_refused_and_not_detected(string name)
    {
        var docx = MakeDocx(DocumentXml("<w:p/>"), zip => Write(zip, name, "x"));
        var importer = new DocxImporter();
        var source = new ImportSource("unsafe.docx", docx);

        var detected = importer.Detect(source);

        Assert.Equal(0, detected.Confidence);
        Assert.StartsWith("unsafe archive", detected.Reason, StringComparison.Ordinal);
        Assert.Throws<ImportLimitException>(() => importer.Parse(source));
    }

    [Fact]
    public void A_symbolic_link_entry_is_refused()
    {
        var docx = MakeDocx(DocumentXml("<w:p/>"), zip =>
        {
            var link = Write(zip, "word/link", "../../secret");
            link.ExternalAttributes = unchecked((int)(0xA1EDu << 16));
        });

        var error = Assert.Throws<ImportLimitException>(() => new DocxImporter().Parse(new ImportSource("link.docx", docx)));

        Assert.Contains("symbolic link", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_device_file_entry_is_refused()
    {
        var docx = MakeDocx(DocumentXml("<w:p/>"), zip =>
        {
            var device = Write(zip, "word/dev", "x");
            device.ExternalAttributes = unchecked((int)(0x21B6u << 16));
        });

        var error = Assert.Throws<ImportLimitException>(() => new DocxImporter().Parse(new ImportSource("dev.docx", docx)));

        Assert.Contains("special file", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Too_many_archive_entries_are_refused()
    {
        var docx = MakeDocx(DocumentXml("<w:p/>"), zip =>
        {
            Write(zip, "a.txt", "1");
            Write(zip, "b.txt", "2");
        });
        var importer = new DocxImporter(new ImportLimits(maxArchiveEntries: 3));

        var error = Assert.Throws<ImportLimitException>(() => importer.Parse(new ImportSource("many.docx", docx)));

        Assert.Contains("entries", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deeply_nested_xml_is_refused()
    {
        var nested = string.Concat(Enumerable.Repeat("<w:x>", 200)) + string.Concat(Enumerable.Repeat("</w:x>", 200));
        var docx = MakeDocx(DocumentXml(nested));
        var importer = new DocxImporter(new ImportLimits(maxNestingDepth: 50));

        var error = Assert.Throws<ImportLimitException>(() => importer.Parse(new ImportSource("deep.docx", docx)));

        Assert.Contains("nests deeper", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_type_declaration_is_still_refused()
    {
        var xml = "<?xml version=\"1.0\"?><!DOCTYPE d [<!ENTITY x \"boom\">]><w:document xmlns:w=\"" + _wordNamespace + "\"><w:body><w:p><w:r><w:t>&x;</w:t></w:r></w:p></w:body></w:document>";
        var docx = MakeDocx(xml);

        Assert.ThrowsAny<Exception>(() => new DocxImporter().Parse(new ImportSource("dtd.docx", docx)));
    }

    [Fact]
    public void The_pipeline_refuses_a_source_over_the_size_cap_before_detection()
    {
        var pipeline = new ImportPipeline([new PlainTextImporter()], new ImportLimits(maxSourceBytes: 10));

        var result = pipeline.Run(new ImportSource("big.txt", Encoding.UTF8.GetBytes("this is longer than ten bytes")), new HashSet<string>());

        Assert.Equal(ImportStatus.Failed, result.Status);
        Assert.Contains("over the limit", result.Message, StringComparison.Ordinal);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void The_pipeline_refuses_a_record_over_the_record_size_cap()
    {
        var pipeline = new ImportPipeline([new PlainTextImporter()], new ImportLimits(maxRecordBytes: 8));

        var result = pipeline.Run(new ImportSource("note.txt", Encoding.UTF8.GetBytes("this record body is too long")), new HashSet<string>());

        Assert.Equal(ImportStatus.Failed, result.Status);
        Assert.Contains("over the limit", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pipeline_accepts_a_source_at_the_cap()
    {
        var pipeline = new ImportPipeline([new PlainTextImporter()], new ImportLimits(maxSourceBytes: 5, maxRecordBytes: 5));

        var result = pipeline.Run(new ImportSource("n.txt", Encoding.UTF8.GetBytes("12345")), new HashSet<string>());

        Assert.Equal(ImportStatus.Ready, result.Status);
    }
}
