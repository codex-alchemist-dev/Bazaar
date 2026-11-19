using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace Bazaar.Server.Portability;

/// <summary>
/// Word (.docx) documents: one paragraph per line, taken from word/document.xml. Detection is by package
/// structure, never by extension alone. External entities and DTDs are never processed.
/// </summary>
public sealed class DocxImporter : IPortabilityImporter
{
    private const string _requiredContentTypes = "[Content_Types].xml";
    private const string _documentPart = "word/document.xml";
    private const int _maxTitleLength = 120;
    private static readonly XNamespace _w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private readonly ImportLimits _limits;

    public DocxImporter() : this(ImportLimits.Default)
    {
    }

    public DocxImporter(ImportLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        _limits = limits;
    }

    public string Id => "bazaar.import.docx";
    public string Version => "1.0.0";

    public DetectResult Detect(ImportSource source)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(source.Content, writable: false), ZipArchiveMode.Read);
            ArchiveGuard.CheckArchive(zip, _limits);
            var names = zip.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
            if (names.Contains(_requiredContentTypes) && names.Contains(_documentPart))
                return new DetectResult(0.95, "docx package");
            return new DetectResult(0, "zip without a docx document part");
        }
        catch (ImportLimitException ex)
        {
            // A zip that breaks a safety rule is not offered as a candidate. Parse would refuse it too.
            return new DetectResult(0, "unsafe archive: " + ex.Message);
        }
        catch (Exception)
        {
            // Not a zip container, or a damaged one: this importer does not claim it.
            return new DetectResult(0, "not a zip container");
        }
    }

    public IReadOnlyList<SourceRecord> Parse(ImportSource source)
    {
        using var zip = new ZipArchive(new MemoryStream(source.Content, writable: false), ZipArchiveMode.Read);
        ArchiveGuard.CheckArchive(zip, _limits);
        var entry = zip.GetEntry(_documentPart) ?? throw new InvalidDataException("docx has no word/document.xml");

        // The part is read under a byte cap and an expansion-ratio cap, then scanned for nesting depth, before
        // any tree is built (doc 44 section 8.3).
        var xml = ArchiveGuard.ReadEntry(entry, _limits, _limits.MaxExpandedBytes);
        ArchiveGuard.CheckXmlDepth(xml, _limits);

        var paragraphs = new List<string>();
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = _limits.MaxExpandedBytes,
        };
        using (var reader = XmlReader.Create(new MemoryStream(xml, writable: false), settings))
        {
            var document = XDocument.Load(reader);
            foreach (var paragraph in document.Descendants(_w + "p"))
            {
                paragraphs.Add(string.Concat(paragraph.Descendants(_w + "t").Select(text => text.Value)));
            }
        }

        var body = string.Join('\n', paragraphs);
        var firstNonEmpty = paragraphs.Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0);
        var title = firstNonEmpty is null
            ? Path.GetFileNameWithoutExtension(source.Name)
            : firstNonEmpty.Length > _maxTitleLength ? firstNonEmpty[.._maxTitleLength] : firstNonEmpty;

        return [new SourceRecord(
            SourceKeys.For(source.Name, body),
            "document",
            title,
            body,
            new Dictionary<string, string> { ["format"] = "docx", ["source"] = source.Name })];
    }
}
