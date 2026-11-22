namespace Bazaar.Kodo.Journal;

/// <summary>
/// Appends hash-chained records to the local spill file. Opening the writer verifies the existing
/// chain and refuses to append onto a broken one. A torn last line from an earlier crash is cut off
/// before the first append; complete records are never rewritten.
/// </summary>
public sealed class JournalSpillWriter : IDisposable
{
    private readonly object _gate = new();
    private readonly Stream _file;
    private byte[] _head;
    private long _nextSequence;

    private JournalSpillWriter(Stream file, byte[] head, long nextSequence)
    {
        _file = file;
        _head = head;
        _nextSequence = nextSequence;
    }

    /// <summary>Sequence number the next appended record will get.</summary>
    public long NextSequence
    {
        get
        {
            lock (_gate)
            {
                return _nextSequence;
            }
        }
    }

    /// <summary>Opens or creates the spill file at <paramref name="path"/> for appending.</summary>
    /// <exception cref="InvalidDataException">The existing chain is malformed or does not verify.</exception>
    public static JournalSpillWriter Open(string path) => Open(path, static file => file);

    // The decorate hook lets the fault harness put a failing stream (disk full) over the real file.
    internal static JournalSpillWriter Open(string path, Func<FileStream, Stream> decorate)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        JournalSpillRead existing = JournalSpill.Read(path);
        JournalVerification verification = JournalChain.Verify(existing.Records);
        if (verification.FirstBrokenIndex is int broken)
        {
            throw new InvalidDataException($"Spill chain is broken at record {broken}; refusing to append.");
        }

        FileStream raw = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Stream file = raw;
        try
        {
            file = decorate(raw);
            file.SetLength(existing.ValidLength);
            file.Seek(0, SeekOrigin.End);
        }
        catch
        {
            file.Dispose();
            raw.Dispose();
            throw;
        }

        byte[] head = existing.Records.Count == 0 ? JournalChain.GenesisHash() : existing.Records[^1].Hash;
        return new JournalSpillWriter(file, head, existing.Records.Count);
    }

    /// <summary>
    /// Appends one record, flushes it to disk, and returns it with its hash. Safe to call from several threads.
    /// If the write fails (for example the disk is full) the file is cut back to its last complete record and the
    /// exception is rethrown, so a later append never lands behind a half-written line.
    /// </summary>
    public JournalRecord Append(DateTimeOffset at, string kind, byte[] payload)
    {
        lock (_gate)
        {
            JournalRecord record = JournalChain.Create(_nextSequence, at, kind, payload, _head);
            byte[] line = JournalSpill.FormatLine(record);
            long start = _file.Position;
            try
            {
                _file.Write(line);
                if (_file is FileStream fileStream)
                {
                    fileStream.Flush(flushToDisk: true);
                }
                else
                {
                    _file.Flush();
                }
            }
            catch
            {
                Rollback(start);
                throw;
            }

            _head = record.Hash;
            _nextSequence++;
            return record;
        }
    }

    /// <summary>Closes the spill file.</summary>
    public void Dispose() => _file.Dispose();

    private void Rollback(long start)
    {
        try
        {
            _file.SetLength(start);
            _file.Seek(start, SeekOrigin.Begin);
        }
        catch (IOException)
        {
            // The tail cannot be cut now. The next Open cuts it (it is a torn line without its LF).
        }
        catch (NotSupportedException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
