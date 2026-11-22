using System.IO.Compression;
using System.Text;

namespace Bazaar.Server.Portability;

/// <summary>
/// Doc 44 section 9 adapter over <see cref="DocxImporter"/>. Detect and Parse are delegated unchanged. Map is the
/// shared <see cref="DefaultMapping"/>, which has no format-specific rules yet.
/// </summary>
public sealed class DocxMappingImporter : IMappingImporter
{
    private const string _formatId = "docx";
    private readonly DocxImporter _inner = new();

    public string Id => _inner.Id;
    public string Version => _inner.Version;
    public string FormatId => _formatId;
    public ImporterDeclaration Declaration { get; } = new(_formatId, [ImportTargetKind.VaultFile]);

    public DetectResult Detect(ImportSource source) => _inner.Detect(source);

    public IReadOnlyList<SourceRecord> Parse(ImportSource source) => _inner.Parse(source);

    public MappingResult Map(SourceRecord record, MappingRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return DefaultMapping.Map(record);
    }

    public ImporterSelfTestResult SelfTest()
    {
        try
        {
            var fixture = BuiltInFixture();
            if (Detect(fixture).Confidence <= 0) return new ImporterSelfTestResult(false, "fixture not detected");
            return Parse(fixture).Count > 0
                ? new ImporterSelfTestResult(true)
                : new ImporterSelfTestResult(false, "fixture parsed to no records");
        }
        catch (Exception ex)
        {
            return new ImporterSelfTestResult(false, ex.Message);
        }
    }

    /// <summary>A minimal in-memory .docx package with two paragraphs, built at call time.</summary>
    internal static ImportSource BuiltInFixture()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "[Content_Types].xml", "<Types/>");
            WriteEntry(zip, "word/document.xml",
                "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>"
                + "<w:p><w:r><w:t>Fixture title</w:t></w:r></w:p>"
                + "<w:p><w:r><w:t>Fixture body.</w:t></w:r></w:p>"
                + "</w:body></w:document>");
        }
        return new ImportSource("fixture.docx", buffer.ToArray());
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }
}
