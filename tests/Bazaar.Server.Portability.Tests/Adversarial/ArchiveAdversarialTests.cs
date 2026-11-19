using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Bazaar.Server.Portability.Tests.Adversarial;

// Red team: the uploaded file is the adversary. Each case must fail closed with an ImportLimitException (or an
// ordinary parse failure), never read past the limits.
public class ArchiveAdversarialTests
{
    private const string _w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static ZipArchive Open(byte[] zip) => new(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);

    private static byte[] Zip(params (string Name, string Content)[] entries) => Zip(CompressionLevel.Optimal, entries);

    private static byte[] Zip(CompressionLevel level, params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var s = zip.CreateEntry(name, level).Open();
                s.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        return ms.ToArray();
    }

    private static byte[] Docx(string documentXml, CompressionLevel level = CompressionLevel.Optimal) =>
        Zip(level, ("[Content_Types].xml", "<Types/>"), ("word/document.xml", documentXml));

    private static string Doc(string inner) => $"<w:document xmlns:w=\"{_w}\"><w:body>{inner}</w:body></w:document>";

    [Theory]
    [InlineData("\\\\server\\share\\x")]
    [InlineData("\\rooted")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\x")]
    [InlineData("a/../../b")]
    [InlineData("a\\..\\b")]
    [InlineData("a\u0001b")]
    [InlineData("a\nb")]
    [InlineData("a\u007Fb")]
    [InlineData("name.")]
    [InlineData("dir /file")]
    [InlineData("dir./file")]
    [InlineData("trailing ")]
    [InlineData("CON")]
    [InlineData("word/NUL.txt")]
    [InlineData("aux.x.y")]
    [InlineData("com1")]
    [InlineData("LPT9.log")]
    [InlineData("a:b")]
    [InlineData("file.txt:hidden")]
    [InlineData("a\0b")]
    public void Hostile_entry_names_are_refused(string name)
    {
        Assert.Throws<ImportLimitException>(() => ArchiveGuard.CheckEntryName(name));
    }

    [Theory]
    [InlineData("word/document.xml")]
    [InlineData("[Content_Types].xml")]
    [InlineData("word/_rels/.rels")]
    [InlineData("word/")]
    [InlineData("console/readme.txt")]
    [InlineData("com10.txt")]
    public void Ordinary_entry_names_are_accepted(string name)
    {
        ArchiveGuard.CheckEntryName(name);
    }

    [Fact]
    public void A_seventy_thousand_character_name_is_refused()
    {
        Assert.Throws<ImportLimitException>(() => ArchiveGuard.CheckEntryName(new string('a', 70_000)));
        Assert.Throws<ImportLimitException>(() => ArchiveGuard.CheckEntryName(new string('a', 5_000)));
    }

    [Fact]
    public void Two_entries_with_the_same_exact_name_are_refused()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("word/document.xml");
            zip.CreateEntry("word/document.xml");
        }

        using var read = Open(ms.ToArray());
        var error = Assert.Throws<ImportLimitException>(() => ArchiveGuard.CheckArchive(read, ImportLimits.Default));
        Assert.Contains("two entries", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Entries_that_differ_only_in_case_are_refused()
    {
        using var read = Open(Zip(("word/Document.xml", "a"), ("word/document.xml", "b")));

        var error = Assert.Throws<ImportLimitException>(() => ArchiveGuard.CheckArchive(read, ImportLimits.Default));
        Assert.Contains("case", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_archive_with_too_many_entries_is_refused_before_names_are_scanned()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < 100_000; i++) zip.CreateEntry("e" + i, CompressionLevel.NoCompression);
        }

        using var read = Open(ms.ToArray());
        Assert.Throws<ImportLimitException>(() => ArchiveGuard.CheckArchive(read, ImportLimits.Default));
    }

    [Fact]
    public void A_zip_bomb_is_stopped_by_the_ratio_or_byte_cap_and_a_docx_parse_fails_cleanly()
    {
        var bomb = Docx(Doc(new string(' ', 80 * 1024 * 1024)), CompressionLevel.SmallestSize);
        var importer = new DocxImporter(new ImportLimits(maxExpandedBytes: 8 * 1024 * 1024));

        Assert.Throws<ImportLimitException>(() => importer.Parse(new ImportSource("b.docx", bomb)));
    }

    [Fact]
    public void A_nested_zip_is_just_bytes_and_is_never_opened()
    {
        var inner = Zip(("x.txt", "inner"));
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var s = zip.CreateEntry("word/document.xml").Open();
            s.Write(inner);
        }

        // The "document" is a zip, not XML: the parse fails as an ordinary error, nothing is extracted recursively.
        Assert.ThrowsAny<Exception>(() => new DocxImporter().Parse(new ImportSource("n.docx", ms.ToArray())));
    }

    [Fact]
    public void A_symlink_or_device_entry_is_refused()
    {
        foreach (var type in new[] { 0xA000, 0x2000, 0x6000, 0x1000, 0xC000 })
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                zip.CreateEntry("link").ExternalAttributes = type << 16;
            }
            using var read = Open(ms.ToArray());
            Assert.Throws<ImportLimitException>(() => ArchiveGuard.CheckArchive(read, ImportLimits.Default));
        }
    }

    [Fact]
    public void A_forged_small_declared_size_cannot_make_the_reader_read_more_than_the_budget()
    {
        var zip = Zip(CompressionLevel.SmallestSize, ("big.bin", new string('a', 4 * 1024 * 1024)));
        PatchCentralDirectory(zip, uncompressed: 10);
        using var read = Open(zip);
        var entry = read.Entries.Single();

        // Either the declared size is rejected, or the read stops at the budget; never the full 4 MiB handed back.
        try
        {
            var bytes = ArchiveGuard.ReadEntry(entry, ImportLimits.Default, 1024 * 1024);
            Assert.True(bytes.Length <= 1024 * 1024);
        }
        catch (Exception ex) when (ex is ImportLimitException or InvalidDataException)
        {
            // Fail closed.
        }
    }

    [Fact]
    public void Zero_compressed_length_with_content_is_refused()
    {
        var zip = Zip(CompressionLevel.NoCompression, ("a.bin", "content"));
        PatchCentralDirectory(zip, compressed: 0);
        using var read = Open(zip);

        var error = Assert.Throws<ImportLimitException>(() => ArchiveGuard.ReadEntry(read.Entries.Single(), ImportLimits.Default, 1024));
        Assert.Contains("no compressed data", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_forged_huge_compressed_length_saturates_instead_of_throwing_overflow()
    {
        // A zip64 central directory entry claiming a compressed length near long.MaxValue. The overflow used to be thrown
        // before the entry was opened; .NET then refuses the corrupt local header, which is fine (fail closed).
        var zip = Zip64Forged(compressed: long.MaxValue - 5, uncompressed: 4);

        try
        {
            using var read = Open(zip);
            var bytes = ArchiveGuard.ReadEntry(read.Entries.Single(), ImportLimits.Default, 1024);
            Assert.True(bytes.Length <= 1024);
        }
        catch (Exception ex)
        {
            Assert.IsNotType<OverflowException>(ex);
        }
    }

    [Fact]
    public void The_total_byte_budget_spans_entries()
    {
        using var read = Open(Zip(CompressionLevel.NoCompression, ("a", new string('a', 600)), ("b", new string('b', 600))));
        var budget = new ArchiveBudget(1000);

        ArchiveGuard.ReadEntry(read.Entries[0], ImportLimits.Default, budget);

        Assert.Throws<ImportLimitException>(() => ArchiveGuard.ReadEntry(read.Entries[1], ImportLimits.Default, budget));
        Assert.Equal(400, budget.Remaining);
    }

    [Fact]
    public void A_billion_laughs_dtd_is_refused()
    {
        var xml = "<?xml version=\"1.0\"?><!DOCTYPE lolz [<!ENTITY lol \"lol\"><!ENTITY lol2 \"&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;\"><!ENTITY lol3 \"&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;\">]>"
            + $"<w:document xmlns:w=\"{_w}\"><w:body><w:p><w:r><w:t>&lol3;</w:t></w:r></w:p></w:body></w:document>";

        Assert.ThrowsAny<Exception>(() => new DocxImporter().Parse(new ImportSource("l.docx", Docx(xml))));
    }

    [Fact]
    public void An_external_entity_is_never_resolved()
    {
        var secret = Path.Combine(Path.GetTempPath(), "bazaar-xxe-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(secret, "TOP-SECRET-CONTENT");
        try
        {
            var xml = $"<?xml version=\"1.0\"?><!DOCTYPE d [<!ENTITY x SYSTEM \"file://{secret}\">]><w:document xmlns:w=\"{_w}\"><w:body><w:p><w:r><w:t>&x;</w:t></w:r></w:p></w:body></w:document>";

            var failure = Record.Exception(() => new DocxImporter().Parse(new ImportSource("x.docx", Docx(xml))));

            Assert.NotNull(failure);
            Assert.DoesNotContain("TOP-SECRET", failure.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(secret);
        }
    }

    [Fact]
    public void Nesting_beyond_the_limit_is_refused()
    {
        var limits = new ImportLimits(maxNestingDepth: 20);
        var xml = Doc(string.Concat(Enumerable.Repeat("<a>", 5000)) + string.Concat(Enumerable.Repeat("</a>", 5000)));

        Assert.Throws<ImportLimitException>(() => new DocxImporter(limits).Parse(new ImportSource("d.docx", Docx(xml))));
    }

    [Fact]
    public void A_document_over_the_character_cap_is_refused_by_the_xml_reader()
    {
        var limits = new ImportLimits(maxExpandedBytes: 4096);
        var xml = Encoding.UTF8.GetBytes(Doc("<w:p><w:r><w:t>" + new string('x', 20_000) + "</w:t></w:r></w:p>"));

        Assert.ThrowsAny<Exception>(() => ArchiveGuard.CheckXmlDepth(xml, limits));
    }

    // Offsets in the central directory file header: signature 4, versions 4, flags 2, method 2, time 4, crc 4, then
    // compressed size at 20 and uncompressed size at 24.
    private static void PatchCentralDirectory(byte[] zip, uint? compressed = null, uint? uncompressed = null)
    {
        var at = zip.AsSpan().LastIndexOf(new byte[] { 0x50, 0x4B, 0x01, 0x02 });
        Assert.True(at >= 0);
        if (compressed is { } c) BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(at + 20), c);
        if (uncompressed is { } u) BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(at + 24), u);
    }

    private static byte[] Zip64Forged(long compressed, long uncompressed)
    {
        var name = Encoding.ASCII.GetBytes("a.bin");
        var data = "data"u8.ToArray();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        // Local header (stored, sizes in the zip64 extra).
        w.Write(0x04034B50); w.Write((ushort)45); w.Write((ushort)0); w.Write((ushort)0); w.Write(0); w.Write(0);
        w.Write(data.Length); w.Write(data.Length); w.Write((ushort)name.Length); w.Write((ushort)0);
        w.Write(name);
        w.Write(data);
        var cd = (int)ms.Position;
        w.Write(0x02014B50); w.Write((ushort)45); w.Write((ushort)45); w.Write((ushort)0); w.Write((ushort)0); w.Write(0); w.Write(0);
        w.Write(uint.MaxValue); w.Write(uint.MaxValue); w.Write((ushort)name.Length); w.Write((ushort)20); w.Write((ushort)0);
        w.Write((ushort)0); w.Write((ushort)0); w.Write(0); w.Write(0);
        w.Write(name);
        w.Write((ushort)1); w.Write((ushort)16); w.Write(uncompressed); w.Write(compressed);
        var cdSize = (int)ms.Position - cd;
        w.Write(0x06054B50); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)1);
        w.Write(cdSize); w.Write(cd); w.Write((ushort)0);
        return ms.ToArray();
    }
}
