using System.IO.Compression;
using System.Text;

namespace Bazaar.Portability.Tests;

public class DocxImporterTests
{
    private const string _wordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static byte[] MakeDocx(params string[] paragraphs)
    {
        var body = string.Concat(paragraphs.Select(p => "<w:p><w:r><w:t>" + p + "</w:t></w:r></w:p>"));
        var document = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<w:document xmlns:w=\"" + _wordNamespace + "\"><w:body>" + body + "</w:body></w:document>";
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types/>");
            WriteEntry(zip, "word/document.xml", document);
        }
        return buffer.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    [Fact]
    public void Detects_docx_package_by_structure()
    {
        var importer = new DocxImporter();
        var result = importer.Detect(new ImportSource("report.docx", MakeDocx("Hello")));
        Assert.Equal(0.95, result.Confidence);
    }

    [Fact]
    public void Plain_text_is_rejected_by_docx_detector()
    {
        var importer = new DocxImporter();
        var result = importer.Detect(new ImportSource("notes.txt", Encoding.UTF8.GetBytes("just some text")));
        Assert.Equal(0, result.Confidence);
    }

    [Fact]
    public void Zip_without_document_part_is_rejected()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "readme.txt", "not a word document");
        }
        var importer = new DocxImporter();
        Assert.Equal(0, importer.Detect(new ImportSource("odd.zip", buffer.ToArray())).Confidence);
    }

    [Fact]
    public void Parses_title_and_one_line_per_paragraph()
    {
        var importer = new DocxImporter();
        var source = new ImportSource("report.docx", MakeDocx("Quarterly Plan", "Ship the importer."));

        var records = importer.Parse(source);

        var record = Assert.Single(records);
        Assert.Equal("Quarterly Plan", record.Title);
        Assert.Equal("Quarterly Plan\nShip the importer.", record.Body);
        Assert.Equal("document", record.Kind);
        Assert.Equal("docx", record.Attributes["format"]);
        Assert.Equal("report.docx", record.Attributes["source"]);
        Assert.Equal(SourceKeys.For("report.docx", record.Body), record.SourceKey);
    }

    [Fact]
    public void Title_falls_back_to_file_name_when_no_paragraph_has_text()
    {
        var importer = new DocxImporter();
        var records = importer.Parse(new ImportSource("empty-report.docx", MakeDocx("", "   ")));
        Assert.Equal("empty-report", Assert.Single(records).Title);
    }

    [Fact]
    public void Title_is_truncated_to_120_characters()
    {
        var importer = new DocxImporter();
        var longLine = new string('a', 200);
        var records = importer.Parse(new ImportSource("long.docx", MakeDocx(longLine)));
        Assert.Equal(120, Assert.Single(records).Title.Length);
    }
}
