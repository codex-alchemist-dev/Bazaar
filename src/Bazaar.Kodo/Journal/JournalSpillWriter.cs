namespace Bazaar.Kodo.Journal;

/// <summary>
/// Appends hash-chained records to the local spill file. Opening the writer verifies the existing
/// chain and refuses to append onto a broken one. A torn last line from an earlier crash is cut off
/// before the first append; complete records are never rewritten.
/// </summary>
public sealed class JournalSpillWriter : IDisposable
{
    private readonly FileStream _file;
    private byte[] _head;
    private long _nextSequence;

    private JournalSpillWriter(FileStream file, byte[] head, long nextSequence)
    {
        _file = file;
        _head = head;
        _nextSequence = nextSequence;
    }

    /// <summary>Sequence number the next appended record will get.</summary>
    public long NextSequence => _nextSequence;

    /// <summary>Opens or creates the spill file at <paramref name="path"/> for appending.</summary>
    /// <exception cref="InvalidDataException">The existing chain is malformed or does not verify.</exception>
    public static JournalSpillWriter Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        JournalSpillRead existing = JournalSpill.Read(path);
        JournalVerification verification = JournalChain.Verify(existing.Records);
        if (verification.FirstBrokenIndex is int broken)
        {
            throw new InvalidDataException($"Spill chain is broken at record {broken}; refusing to append.");
        }

        FileStream file = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            file.SetLength(existing.ValidLength);
            file.Seek(0, SeekOrigin.End);
        }
        catch
        {
            file.Dispose();
            throw;
        }

        byte[] head = existing.Records.Count == 0 ? JournalChain.GenesisHash() : existing.Records[^1].Hash;
        return new JournalSpillWriter(file, head, existing.Records.Count);
    }

    /// <summary>Appends one record, flushes it to disk, and returns it with its hash.</summary>
    public JournalRecord Append(DateTimeOffset at, string kind, byte[] payload)
    {
        JournalRecord record = JournalChain.Create(_nextSequence, at, kind, payload, _head);
        _file.Write(JournalSpill.FormatLine(record));
        _file.Flush(flushToDisk: true);

        _head = record.Hash;
        _nextSequence++;
        return record;
    }

    /// <summary>Closes the spill file.</summary>
    public void Dispose() => _file.Dispose();
}
