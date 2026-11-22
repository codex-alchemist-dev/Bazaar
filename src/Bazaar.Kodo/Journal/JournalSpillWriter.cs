namespace Bazaar.Kodo.Journal;

/// <summary>
/// Appends hash-chained records to the local spill file. Opening the writer verifies the existing
/// chain and refuses to append onto a broken one. A torn last line from an earlier crash is cut off
/// before the first append; complete records are never rewritten.
/// </summary>
/// <remarks>
/// With a size bound (doc 46 section 7.5) the file never grows past it for long: when an append takes it over the bound, the oldest
/// records are dropped, the newest kept, and the file is rewritten (to a temporary file, then moved over the old one) with an anchor
/// line that states where the kept records continue the chain. The dropped range is reported through <see cref="TakeGap"/> so the
/// caller can put a gap observation into the journal. No record is invented to fill it. <see cref="Seal"/> moves a fully replayed
/// spill aside and starts a new one that continues the chain.
/// </remarks>
public sealed class JournalSpillWriter : IDisposable
{
    private readonly string _path;
    private readonly long? _maxBytes;
    private FileStream _file;
    private byte[] _head;
    private long _nextSequence;
    private long _length;
    private int _recordCount;
    private JournalGap? _gap;

    private JournalSpillWriter(string path, long? maxBytes, FileStream file, byte[] head, long nextSequence, long length, int recordCount)
    {
        _path = path;
        _maxBytes = maxBytes;
        _file = file;
        _head = head;
        _nextSequence = nextSequence;
        _length = length;
        _recordCount = recordCount;
    }

    /// <summary>Sequence number the next appended record will get.</summary>
    public long NextSequence => _nextSequence;

    /// <summary>Bytes in the file.</summary>
    public long Length => _length;

    /// <summary>Records in the file, not counting any that were dropped or sealed away.</summary>
    public int RecordCount => _recordCount;

    /// <summary>Opens or creates the spill file at <paramref name="path"/> for appending, with no size bound.</summary>
    /// <exception cref="InvalidDataException">The existing chain is malformed or does not verify.</exception>
    public static JournalSpillWriter Open(string path) => Open(path, maxBytes: null);

    /// <summary>
    /// Opens or creates the spill file at <paramref name="path"/> for appending. <paramref name="maxBytes"/> is the size bound, or
    /// null for none. A file already over the bound is cut down by the first append.
    /// </summary>
    /// <exception cref="InvalidDataException">The existing chain is malformed or does not verify.</exception>
    public static JournalSpillWriter Open(string path, long? maxBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (maxBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "The size bound must be positive.");
        }

        JournalSpillRead existing = JournalSpill.Read(path);
        JournalVerification verification = existing.Verify();
        if (verification.FirstBrokenIndex is int broken)
        {
            throw new InvalidDataException($"Spill chain is broken at record {broken}; refusing to append.");
        }

        FileStream file = OpenFile(path, existing.ValidLength);
        byte[] head = existing.Records.Count > 0 ? existing.Records[^1].Hash : existing.Anchor?.PreviousHash ?? JournalChain.GenesisHash();
        long next = existing.Records.Count > 0 ? existing.Records[^1].Sequence + 1 : existing.Anchor?.NextSequence ?? 0;
        return new JournalSpillWriter(path, maxBytes, file, head, next, existing.ValidLength, existing.Records.Count);
    }

    /// <summary>Appends one record, flushes it to disk, and returns it with its hash. May compact the file when it is over the bound.</summary>
    public JournalRecord Append(DateTimeOffset at, string kind, byte[] payload)
    {
        JournalRecord record = JournalChain.Create(_nextSequence, at, kind, payload, _head);
        byte[] line = JournalSpill.FormatLine(record);
        _file.Write(line);
        _file.Flush(flushToDisk: true);

        _head = record.Hash;
        _nextSequence++;
        _length += line.Length;
        _recordCount++;
        if (_maxBytes is long max && JournalSpillBound.IsOver(_length, max))
        {
            Compact(max);
        }

        return record;
    }

    /// <summary>The range dropped at the size bound since the last call, or null. Reading it clears it.</summary>
    public JournalGap? TakeGap()
    {
        JournalGap? gap = _gap;
        _gap = null;
        return gap;
    }

    /// <summary>
    /// Moves the file to <paramref name="sealedPath"/> and starts a new one that continues the chain from the current head
    /// (doc 46 section 7.5, after a successful replay). Returns false, changing nothing, when the file holds no record.
    /// </summary>
    /// <exception cref="IOException">The move failed (for example the target exists); the old file stays active.</exception>
    public bool Seal(string sealedPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(sealedPath);
        if (_recordCount == 0)
        {
            return false;
        }

        _file.Dispose();
        try
        {
            File.Move(_path, sealedPath);
        }
        catch
        {
            _file = OpenFile(_path, _length);
            throw;
        }

        JournalAnchor anchor = new(_nextSequence, _head, DroppedRecords: 0);
        byte[] line = anchor.FormatLine();
        _file = new FileStream(_path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        _file.Write(line);
        _file.Flush(flushToDisk: true);
        _length = line.Length;
        _recordCount = 0;
        return true;
    }

    /// <summary>Closes the spill file.</summary>
    public void Dispose() => _file.Dispose();

    private static FileStream OpenFile(string path, long validLength)
    {
        FileStream file = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            file.SetLength(validLength);
            file.Seek(0, SeekOrigin.End);
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    // Rewrites the file without its oldest records. A failure before the move leaves the old file as it was.
    private void Compact(long maxBytes)
    {
        string temporary = _path + ".compact";
        _file.Dispose();
        try
        {
            try
            {
                CompactTo(temporary, maxBytes);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                TryDelete(temporary);
            }
        }
        finally
        {
            FileInfo info = new(_path);
            _length = info.Length;
            _file = OpenFile(_path, _length);
        }
    }

    private void CompactTo(string temporary, long maxBytes)
    {
        JournalSpillShape shape = JournalSpill.Scan(_path);
        int drop = JournalSpillBound.RecordsToDrop(shape.LineLengths, maxBytes);
        if (drop == 0)
        {
            return;
        }

        long firstSequence = shape.Anchor?.NextSequence ?? 0;
        JournalGap gap = JournalSpillBound.GapOf(firstSequence, drop)!;
        long dropped = (shape.Anchor?.DroppedRecords ?? 0) + drop;

        using (FileStream output = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            int index = -1;
            bool first = true;
            byte[]? lastDroppedHash = null;
            foreach ((byte[] line, _) in JournalSpill.ReadLines(_path))
            {
                if (first && JournalAnchor.IsAnchorLine(line))
                {
                    first = false;
                    continue;
                }

                first = false;
                index++;
                if (index < drop - 1)
                {
                    continue;
                }

                if (index == drop - 1)
                {
                    JournalRecord last = JournalSpill.ParseRecordLine(line, index);
                    if (last.Sequence != gap.LastSequence)
                    {
                        throw new InvalidDataException("Spill sequence does not match its position.");
                    }

                    lastDroppedHash = last.Hash;
                    continue;
                }

                if (index == drop)
                {
                    output.Write(new JournalAnchor(gap.LastSequence + 1, lastDroppedHash!, dropped).FormatLine());
                }

                output.Write(line);
                output.WriteByte((byte)'\n');
            }

            output.Flush(flushToDisk: true);
        }

        File.Move(temporary, _path, overwrite: true);
        _recordCount -= drop;
        _gap = _gap is null ? gap : new JournalGap(_gap.FirstSequence, gap.LastSequence);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file is overwritten by the next compaction.
        }
    }
}
