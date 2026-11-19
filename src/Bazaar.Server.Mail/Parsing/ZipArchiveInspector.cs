using System.Buffers.Binary;
using System.Text;
using Bazaar.Server.Mail.Quarantine;

namespace Bazaar.Server.Mail.Parsing;

/// <summary>
/// Reads a ZIP file's central directory and nothing else (doc 22, attachments). It never decompresses and never walks
/// more than <see cref="MailLimits.MaxArchiveEntries"/> entries, so a zip bomb costs one directory read. ZIP64 is
/// refused as malformed: a ZIP64 archive inside a message capped at tens of MiB is not ordinary mail.
/// Nesting is judged from entry names only; a nested archive stored under a plain name is not found here.
/// </summary>
public static class ZipArchiveInspector
{
    private const uint _endSignature = 0x06054b50;
    private const uint _entrySignature = 0x02014b50;
    private const int _endFixedLength = 22;
    private const int _entryFixedLength = 46;

    private static readonly string[] _archiveExtensions =
        [".zip", ".jar", ".7z", ".rar", ".gz", ".tgz", ".tar", ".bz2", ".xz", ".zst", ".cab", ".iso", ".lz", ".lzma", ".arj"];

    /// <summary>Inspects an attachment's bytes.</summary>
    /// <returns>The declared facts, or null when the bytes are not a ZIP file (no end-of-directory record).</returns>
    public static ArchiveFacts? Inspect(ReadOnlySpan<byte> data, MailLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var end = FindEndRecord(data);
        if (end < 0)
        {
            return null;
        }

        var record = data[end..];
        int declaredEntries = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
        var directorySize = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
        var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);

        if (declaredEntries == ushort.MaxValue || directorySize == uint.MaxValue || directoryOffset == uint.MaxValue)
        {
            return Bad(declaredEntries);
        }

        if (declaredEntries > limits.MaxArchiveEntries)
        {
            // Over the limit: report it without reading the directory.
            return Bad(declaredEntries);
        }

        if (directoryOffset > (uint)end || directorySize > (uint)end - directoryOffset)
        {
            return Bad(declaredEntries);
        }

        var directory = data.Slice((int)directoryOffset, (int)directorySize);
        long compressed = 0, uncompressed = 0;
        var nested = false;
        var position = 0;
        for (var i = 0; i < declaredEntries; i++)
        {
            if (directory.Length - position < _entryFixedLength
                || BinaryPrimitives.ReadUInt32LittleEndian(directory[position..]) != _entrySignature)
            {
                return new ArchiveFacts(declaredEntries, compressed, uncompressed, nested, Malformed: true);
            }

            var entry = directory[position..];
            var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(entry[20..]);
            var uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(entry[24..]);
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(entry[28..]);
            int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(entry[30..]);
            int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(entry[32..]);
            var total = _entryFixedLength + nameLength + extraLength + commentLength;
            if (entry.Length < total || compressedSize == uint.MaxValue || uncompressedSize == uint.MaxValue)
            {
                return new ArchiveFacts(declaredEntries, compressed, uncompressed, nested, Malformed: true);
            }

            compressed += compressedSize;
            uncompressed += uncompressedSize;
            var name = Encoding.UTF8.GetString(entry.Slice(_entryFixedLength, nameLength));
            nested |= HasArchiveExtension(name);
            position += total;
        }

        return new ArchiveFacts(declaredEntries, compressed, uncompressed, nested, Malformed: false);
    }

    private static ArchiveFacts Bad(int entries) => new(entries, 0, 0, ContainsArchive: false, Malformed: true);

    private static bool HasArchiveExtension(string entryName)
    {
        var name = entryName.Trim().TrimEnd('/');
        foreach (var extension in _archiveExtensions)
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Finds the end-of-directory record by scanning back over at most the 64 KiB comment the format allows.</summary>
    private static int FindEndRecord(ReadOnlySpan<byte> data)
    {
        if (data.Length < _endFixedLength)
        {
            return -1;
        }

        var lowest = Math.Max(0, data.Length - _endFixedLength - ushort.MaxValue);
        for (var i = data.Length - _endFixedLength; i >= lowest; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data[i..]) == _endSignature)
            {
                var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(data[(i + 20)..]);
                if (i + _endFixedLength + commentLength == data.Length)
                {
                    return i;
                }
            }
        }

        return -1;
    }
}
