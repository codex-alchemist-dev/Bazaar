using System.Globalization;
using System.Text;

namespace Bazaar.Kodo.Journal;

/// <summary>
/// Where a spill file continues the chain (doc 46 section 7.5). A spill that starts here holds no record before
/// <see cref="NextSequence"/>: either the oldest ones were dropped at the size bound (<see cref="DroppedRecords"/> says how many, so
/// the gap is stated and never filled), or the file was started after the previous one was sealed (<see cref="DroppedRecords"/> 0).
/// The first record of the file must carry <see cref="NextSequence"/> and link to <see cref="PreviousHash"/>, so the segment still
/// verifies against the chain it came from.
/// </summary>
/// <param name="NextSequence">Sequence of the first record the file holds (or the next one it will hold when it is empty).</param>
/// <param name="PreviousHash">Hash of the record just before it, 32 bytes.</param>
/// <param name="DroppedRecords">Records dropped from the chain at the size bound before this point, cumulative in this file's life.</param>
public sealed record JournalAnchor(long NextSequence, byte[] PreviousHash, long DroppedRecords)
{
    private const string _tag = "@anchor";
    private const char _separator = ' ';
    private const int _fieldCount = 4;

    /// <summary>Formats the anchor as the first spill line, including its LF terminator.</summary>
    public byte[] FormatLine() => Encoding.ASCII.GetBytes(string.Join(
        _separator,
        _tag,
        NextSequence.ToString(CultureInfo.InvariantCulture),
        Convert.ToHexString(PreviousHash),
        DroppedRecords.ToString(CultureInfo.InvariantCulture)) + '\n');

    /// <summary>True when the line (without its LF) is an anchor line.</summary>
    public static bool IsAnchorLine(ReadOnlySpan<byte> line) => line.StartsWith("@anchor"u8);

    /// <summary>Parses an anchor line (without its LF).</summary>
    /// <exception cref="InvalidDataException">The line is malformed.</exception>
    public static JournalAnchor Parse(ReadOnlySpan<byte> line)
    {
        string[] fields = Encoding.ASCII.GetString(line).Split(_separator);
        if (fields.Length != _fieldCount
            || fields[0] != _tag
            || !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out long next)
            || !long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out long dropped)
            || fields[2].Length != JournalChain.HashLength * 2)
        {
            throw new InvalidDataException("Spill anchor line is malformed.");
        }

        try
        {
            return new JournalAnchor(next, Convert.FromHexString(fields[2]), dropped);
        }
        catch (FormatException error)
        {
            throw new InvalidDataException("Spill anchor line has invalid encoding.", error);
        }
    }
}

/// <summary>A run of records the spill dropped at its size bound. Sequences are inclusive. The records are gone; nothing replaces them.</summary>
/// <param name="FirstSequence">First dropped sequence.</param>
/// <param name="LastSequence">Last dropped sequence.</param>
public sealed record JournalGap(long FirstSequence, long LastSequence)
{
    /// <summary>Number of records in the gap.</summary>
    public long Count => LastSequence - FirstSequence + 1;
}
