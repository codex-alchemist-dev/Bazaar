using System.IO.Compression;
using System.Xml;

namespace Bazaar.Server.Portability;

/// <summary>
/// Archive and XML safety checks for import sources (doc 44 section 8.3): entry-name path checks, special-file
/// rejection, an entry-count cap, a bounded decompression with an expansion-ratio cap, and an XML depth cap.
/// Nothing here extracts to disk; entries are read into memory under a hard byte cap.
/// </summary>
internal static class ArchiveGuard
{
    // Below this expanded size the ratio is not checked: tiny parts compress oddly, and the absolute cap still holds.
    private const long _ratioCheckThresholdBytes = 1024 * 1024;

    // Unix file type bits (the high 16 bits of a zip entry's external attributes, when made on Unix).
    private const int _fileTypeMask = 0xF000;
    private const int _fileTypeFifo = 0x1000;
    private const int _fileTypeCharDevice = 0x2000;
    private const int _fileTypeBlockDevice = 0x6000;
    private const int _fileTypeSymlink = 0xA000;
    private const int _fileTypeSocket = 0xC000;

    /// <summary>Checks the entry count and every entry name and type. Throws <see cref="ImportLimitException"/>.</summary>
    public static void CheckArchive(ZipArchive zip, ImportLimits limits)
    {
        ArgumentNullException.ThrowIfNull(zip);
        ArgumentNullException.ThrowIfNull(limits);

        if (zip.Entries.Count > limits.MaxArchiveEntries)
            throw new ImportLimitException($"archive has {zip.Entries.Count} entries, over the limit of {limits.MaxArchiveEntries}");

        foreach (var entry in zip.Entries)
        {
            CheckEntryName(entry.FullName);
            CheckEntryType(entry);
        }
    }

    /// <summary>
    /// Reads one entry fully into memory. Fails when the declared or actual expanded size passes
    /// <paramref name="byteBudget"/>, or when the expansion ratio passes the limit.
    /// </summary>
    public static byte[] ReadEntry(ZipArchiveEntry entry, ImportLimits limits, long byteBudget)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(limits);

        var declared = entry.Length;
        if (declared > byteBudget)
            throw new ImportLimitException($"entry '{entry.FullName}' expands to {declared} bytes, over the limit of {byteBudget}");

        // The declared length comes from the archive and can be wrong, so the actual bytes are counted below.
        long allowed = byteBudget;
        if (entry.CompressedLength > 0)
        {
            var ratioBound = checked(entry.CompressedLength * limits.MaxExpansionRatio);
            allowed = Math.Min(allowed, Math.Max(ratioBound, _ratioCheckThresholdBytes));
        }
        else if (declared > 0)
        {
            throw new ImportLimitException($"entry '{entry.FullName}' claims content with no compressed data");
        }

        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += read;
            if (total > allowed)
                throw new ImportLimitException($"entry '{entry.FullName}' expands past the limit ({limits.MaxExpansionRatio}:1 ratio or {byteBudget} bytes)");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>Scans XML for nesting deeper than the limit, without building a tree. DTDs are refused.</summary>
    public static void CheckXmlDepth(byte[] xml, ImportLimits limits)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ArgumentNullException.ThrowIfNull(limits);

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = limits.MaxExpandedBytes,
        };
        using var reader = XmlReader.Create(new MemoryStream(xml, writable: false), settings);
        while (reader.Read())
        {
            if (reader.Depth > limits.MaxNestingDepth)
                throw new ImportLimitException($"XML nests deeper than the limit of {limits.MaxNestingDepth}");
        }
    }

    private static void CheckEntryName(string name)
    {
        if (name.Length == 0 || name.Contains('\0', StringComparison.Ordinal))
            throw new ImportLimitException("archive entry has an empty or invalid name");
        if (name[0] is '/' or '\\')
            throw new ImportLimitException($"archive entry '{name}' has an absolute path");
        if (name.Length >= 2 && name[1] == ':' && char.IsAsciiLetter(name[0]))
            throw new ImportLimitException($"archive entry '{name}' has a drive path");
        foreach (var segment in name.Split('/', '\\'))
        {
            if (segment == "..")
                throw new ImportLimitException($"archive entry '{name}' contains a '..' segment");
        }
    }

    private static void CheckEntryType(ZipArchiveEntry entry)
    {
        var type = (entry.ExternalAttributes >> 16) & _fileTypeMask;
        if (type is _fileTypeSymlink)
            throw new ImportLimitException($"archive entry '{entry.FullName}' is a symbolic link");
        if (type is _fileTypeFifo or _fileTypeCharDevice or _fileTypeBlockDevice or _fileTypeSocket)
            throw new ImportLimitException($"archive entry '{entry.FullName}' is a special file");
    }
}
