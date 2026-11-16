using System.Text;

namespace Bazaar.Kodo.Journal;

/// <summary>
/// Result of reading a spill file. <see cref="ValidLength"/> is the byte offset just past the last
/// complete line; <see cref="TornTailIgnored"/> is true when bytes after it were ignored.
/// </summary>
public sealed record JournalSpillRead(IReadOnlyList<JournalRecord> Records, bool TornTailIgnored, long ValidLength);

/// <summary>
/// Reads the local append-only spill file (doc 46 section 7.5). One record per line, in the form
/// <c>&lt;hex previous hash&gt; &lt;hex hash&gt; &lt;base64 canonical bytes&gt;</c> terminated by LF.
/// A final line without its LF is a torn write and is ignored. Reading does not verify the chain;
/// use <see cref="JournalChain.Verify"/> on the result.
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
        using Stream stream = new BufferedStream(File.OpenRead(path), 64 * 1024);
        using MemoryStream line = new();
        long consumed = 0;
        long validLength = 0;
        int next;
        while ((next = stream.ReadByte()) >= 0)
        {
            consumed++;
            if (next == _newLine)
            {
                records.Add(ParseLine(line.ToArray(), records.Count));
                line.SetLength(0);
                validLength = consumed;
            }
            else
            {
                line.WriteByte((byte)next);
            }
        }

        bool torn = line.Length > 0;
        return new JournalSpillRead(records, torn, validLength);
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
