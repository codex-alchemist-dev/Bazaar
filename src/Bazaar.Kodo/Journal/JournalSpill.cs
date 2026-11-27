using System.Text;

namespace Bazaar.Kodo.Journal;

/// <summary>
/// Result of reading a spill file. <see cref="ValidLength"/> is the byte offset just past the last
/// complete line; <see cref="TornTailIgnored"/> is true when bytes after it were ignored. <see cref="Anchor"/> is set when the file
/// starts with an anchor line (it dropped its oldest records at the size bound, or follows a sealed spill), and then the records
/// continue the chain from it instead of from the genesis hash.
/// </summary>
public sealed record JournalSpillRead(IReadOnlyList<JournalRecord> Records, bool TornTailIgnored, long ValidLength, JournalAnchor? Anchor = null)
{
    /// <summary>Verifies the records against the genesis hash, or against <see cref="Anchor"/> when the file has one.</summary>
    public JournalVerification Verify() => Anchor is null ? JournalChain.Verify(Records) : JournalChain.Verify(Records, Anchor);
}

/// <summary>The shape of a spill file read line by line, without keeping the records: what compaction needs.</summary>
/// <param name="Anchor">The anchor line, or null.</param>
/// <param name="LineLengths">Byte length of each record line including its LF, oldest first.</param>
/// <param name="ValidLength">Offset just past the last complete line.</param>
public sealed record JournalSpillShape(JournalAnchor? Anchor, IReadOnlyList<int> LineLengths, long ValidLength);

/// <summary>
/// Reads the local append-only spill file (doc 46 section 7.5). One record per line, in the form
/// <c>&lt;hex previous hash&gt; &lt;hex hash&gt; &lt;base64 canonical bytes&gt;</c> terminated by LF.
/// A final line without its LF is a torn write and is ignored. A first line of the form
/// <c>@anchor &lt;next sequence&gt; &lt;hex previous hash&gt; &lt;dropped count&gt;</c> says the file continues a chain it no longer holds
/// the start of. Reading does not verify the chain; use <see cref="JournalSpillRead.Verify"/> on the result.
/// </summary>
public static class JournalSpill
{
    private const char _lineSeparator = ' ';
    private const byte _newLine = (byte)'\n';
    private const int _fieldCount = 3;

    /// <summary>Reads every complete record in <paramref name="path"/>. A missing file reads as empty.</summary>
    /// <exception cref="InvalidDataException">A complete line is malformed.</exception>
    public static JournalSpillRead Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!File.Exists(path))
        {
            return new JournalSpillRead([], TornTailIgnored: false, ValidLength: 0);
        }

        List<JournalRecord> records = [];
        JournalAnchor? anchor = null;
        long validLength = 0;
        bool first = true;
        foreach ((byte[] line, long end) in ReadLines(path))
        {
            if (first && JournalAnchor.IsAnchorLine(line))
            {
                anchor = JournalAnchor.Parse(line);
            }
            else
            {
                records.Add(ParseLine(line, records.Count));
            }

            first = false;
            validLength = end;
        }

        return new JournalSpillRead(records, new FileInfo(path).Length > validLength, validLength, anchor);
    }

    /// <summary>Scans a spill file for its anchor and the byte length of each record line, without decoding the records.</summary>
    /// <exception cref="InvalidDataException">The anchor line is malformed.</exception>
    public static JournalSpillShape Scan(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!File.Exists(path))
        {
            return new JournalSpillShape(null, [], 0);
        }

        List<int> lengths = [];
        JournalAnchor? anchor = null;
        long validLength = 0;
        bool first = true;
        foreach ((byte[] line, long end) in ReadLines(path))
        {
            if (first && JournalAnchor.IsAnchorLine(line))
            {
                anchor = JournalAnchor.Parse(line);
            }
            else
            {
                lengths.Add(line.Length + 1);
            }

            first = false;
            validLength = end;
        }

        return new JournalSpillShape(anchor, lengths, validLength);
    }

    /// <summary>Parses one record line (without its LF). Used by compaction to read the last dropped record.</summary>
    /// <exception cref="InvalidDataException">The line is malformed.</exception>
    public static JournalRecord ParseRecordLine(byte[] line, int recordIndex) => ParseLine(line, recordIndex);

    // Complete lines (LF-terminated) with the offset just past each. A final line without its LF is not returned.
    internal static IEnumerable<(byte[] Line, long End)> ReadLines(string path)
    {
        using Stream stream = new BufferedStream(File.OpenRead(path), 64 * 1024);
        using MemoryStream line = new();
        long consumed = 0;
        int next;
        while ((next = stream.ReadByte()) >= 0)
        {
            consumed++;
            if (next == _newLine)
            {
                yield return (line.ToArray(), consumed);
                line.SetLength(0);
            }
            else
            {
                line.WriteByte((byte)next);
            }
        }
    }

    /// <summary>Formats one spill line for a record, including its LF terminator.</summary>
    public static byte[] FormatLine(JournalRecord record)
    {
        string text = string.Join(
            _lineSeparator,
            Convert.ToHexString(record.PreviousHash),
            Convert.ToHexString(record.Hash),
            Convert.ToBase64String(JournalCodec.Encode(record))) + '\n';
        return Encoding.ASCII.GetBytes(text);
    }

    private static JournalRecord ParseLine(byte[] bytes, int recordIndex)
    {
        string[] fields = Encoding.ASCII.GetString(bytes).Split(_lineSeparator);
        if (fields.Length != _fieldCount)
        {
            throw new InvalidDataException($"Spill record {recordIndex} is malformed.");
        }

        try
        {
            return JournalCodec.Decode(
                Convert.FromBase64String(fields[2]),
                Convert.FromHexString(fields[0]),
                Convert.FromHexString(fields[1]));
        }
        catch (FormatException error)
        {
            throw new InvalidDataException($"Spill record {recordIndex} has invalid encoding.", error);
        }
    }
}
