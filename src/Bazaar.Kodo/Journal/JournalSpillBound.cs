namespace Bazaar.Kodo.Journal;

/// <summary>
/// The size bound of the local spill file (doc 46 section 7.5), as pure decisions. At the bound the oldest records go and the newest
/// stay; the file is cut down to a share of the bound so it does not compact on every append.
/// </summary>
public static class JournalSpillBound
{
    /// <summary>Bytes kept free for the anchor line a compacted file starts with.</summary>
    public const int AnchorReserve = 160;

    /// <summary>Share of the bound a compacted file is cut down to, in percent.</summary>
    public const int CompactToPercent = 80;

    /// <summary>The settings registry holds megabytes; this is the byte count.</summary>
    public static long BytesFromMegabytes(int megabytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(megabytes);
        return megabytes * 1024L * 1024L;
    }

    /// <summary>True when a file of <paramref name="fileLength"/> bytes is over <paramref name="maxBytes"/>.</summary>
    public static bool IsOver(long fileLength, long maxBytes) => fileLength > maxBytes;

    /// <summary>
    /// How many of the oldest records to drop. <paramref name="lineLengths"/> are the byte lengths of the record lines, oldest first.
    /// Nothing is dropped while the file fits <paramref name="maxBytes"/>. Otherwise the oldest go until the rest, plus the anchor
    /// reserve, fits the compaction target. The newest record always stays, even when it alone exceeds the target.
    /// </summary>
    public static int RecordsToDrop(IReadOnlyList<int> lineLengths, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(lineLengths);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        long total = AnchorReserve;
        foreach (int length in lineLengths)
        {
            total += length;
        }

        if (total <= maxBytes)
        {
            return 0;
        }

        long target = maxBytes / 100 * CompactToPercent;
        int dropped = 0;
        while (total > target && dropped < lineLengths.Count - 1)
        {
            total -= lineLengths[dropped];
            dropped++;
        }

        return dropped;
    }

    /// <summary>The gap a drop of <paramref name="dropped"/> records leaves, given the sequence of the file's first record. Null when nothing was dropped.</summary>
    public static JournalGap? GapOf(long firstSequenceInFile, int dropped) =>
        dropped <= 0 ? null : new JournalGap(firstSequenceInFile, firstSequenceInFile + dropped - 1);

    /// <summary>The name a sealed spill is moved to: the active name, a marker and the UTC time. Sorts by time.</summary>
    public static string SealedName(string activePath, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrEmpty(activePath);
        return activePath + ".sealed-" + at.ToUniversalTime().ToString("yyyyMMdd'T'HHmmssfff'Z'", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>True when <paramref name="path"/> is a sealed archive of the active spill at <paramref name="activePath"/>.</summary>
    public static bool IsSealedName(string activePath, string path) =>
        path.StartsWith(activePath + ".sealed-", StringComparison.Ordinal);
}
