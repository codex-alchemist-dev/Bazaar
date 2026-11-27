using System.Text;
using Bazaar.Kodo.Anchoring;
using Bazaar.Kodo.Journal;

namespace Bazaar.Server.Kodo;

/// <summary>
/// Kodo's local journal: the hash chain of doc 46 section 6.5, held in memory and, when a data directory is set, appended to the
/// spill file (doc 46 section 7.5). It never throws on append: a refused write marks the journal not writable, which the
/// <c>kodo.self.journal_writable</c> probe reports, and the next append tries to reopen the file.
/// </summary>
internal sealed class KodoJournal : IDisposable
{
    private const int _maxGapMarkers = 3;

    private readonly Lock _gate = new();
    private readonly string? _spillPath;
    private readonly int _capacity;
    private readonly long? _maxSpillBytes;
    private readonly List<JournalRecord> _records = [];
    private JournalSpillWriter? _spill;
    private byte[]? _head;
    private long _next;
    private bool _writable = true;
    private bool _spillSuspended;

    /// <param name="directory">Folder of the spill file, or null for a memory-only journal.</param>
    /// <param name="capacity">Records kept in memory.</param>
    /// <param name="maxSpillBytes">Size bound of the spill file (doc 46 section 7.5), or null for none.</param>
    public KodoJournal(string? directory, int capacity, long? maxSpillBytes = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _maxSpillBytes = maxSpillBytes;
        if (!string.IsNullOrWhiteSpace(directory))
        {
            _spillPath = Path.Combine(directory, "kodo-journal.spill");
            _writable = TryOpenSpill();
        }
    }

    /// <summary>The hash of the newest record, or null while the journal has none.</summary>
    public byte[]? Head
    {
        get
        {
            lock (_gate)
            {
                return _head is null ? null : [.. _head];
            }
        }
    }

    /// <summary>The sequence the next appended record gets: the number of records the chain holds.</summary>
    public long NextSequence
    {
        get
        {
            lock (_gate)
            {
                return _next;
            }
        }
    }

    /// <summary>Records in memory with a sequence of at least <paramref name="sequence"/>, oldest first, at most <paramref name="limit"/>.</summary>
    public IReadOnlyList<JournalRecord> ReadFrom(long sequence, int limit)
    {
        lock (_gate)
        {
            return _records.Where(record => record.Sequence >= sequence).Take(limit).ToArray();
        }
    }

    /// <summary>
    /// Continues the chain from records the durable store already holds, when this journal has none of its own (a lost or absent spill
    /// file). The spill file is left alone for the rest of the run, because a file that does not start at record 0 would not verify on
    /// reopen. Returns false, changing nothing, when the journal already has records or <paramref name="tail"/> is empty.
    /// </summary>
    public bool AdoptTail(IReadOnlyList<JournalRecord> tail)
    {
        ArgumentNullException.ThrowIfNull(tail);
        lock (_gate)
        {
            if (_next != 0 || _records.Count != 0 || tail.Count == 0)
            {
                return false;
            }

            _spill?.Dispose();
            _spill = null;
            _spillSuspended = true;
            _records.AddRange(tail.Count > _capacity ? tail.Skip(tail.Count - _capacity) : tail);
            _head = tail[^1].Hash;
            _next = tail[^1].Sequence + 1;
            _writable = true;
            return true;
        }
    }

    /// <summary>Appends one record and returns it, or null when the journal refused the write.</summary>
    public JournalRecord? Append(DateTimeOffset at, string kind, string summary)
    {
        lock (_gate)
        {
            try
            {
                byte[] payload = Encoding.UTF8.GetBytes(summary);
                JournalRecord record;
                if (_spillPath is not null && !_spillSuspended)
                {
                    if (_spill is null && !TryOpenSpill())
                    {
                        _writable = false;
                        return null;
                    }

                    record = _spill!.Append(at, kind, payload);
                    Remember(record);
                    RecordGaps(at);
                }
                else
                {
                    record = JournalChain.Create(_next, at, kind, payload, _head ?? JournalChain.GenesisHash());
                    Remember(record);
                }

                _writable = true;
                return record;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            {
                _spill?.Dispose();
                _spill = null;
                _writable = false;
                return null;
            }
        }
    }

    /// <summary>
    /// True when the journal has no spill file, or the file is open. A spill that is closed gets one reopen attempt; one that still
    /// cannot be opened (unreadable or its chain broken) is not readable, and <c>kodo.self.spill</c> reports it red.
    /// </summary>
    public bool CheckSpillReadable()
    {
        lock (_gate)
        {
            if (_spillPath is null || _spillSuspended)
            {
                return true;
            }

            return _spill is not null || TryOpenSpill();
        }
    }

    /// <summary>
    /// Seals the spill after a successful replay (doc 46 section 7.5): when the store holds every local record
    /// (<paramref name="storedThrough"/> is the newest), the file is moved aside as a sealed archive and a new one continues the chain.
    /// Archives older than <paramref name="retention"/> are deleted. Returns true when sealed or when there is no spill to seal, and
    /// false when records are still unsent or the move failed (the next pass tries again).
    /// </summary>
    public bool SealSpill(long storedThrough, DateTimeOffset at, TimeSpan retention)
    {
        lock (_gate)
        {
            if (_spillPath is null || _spillSuspended)
            {
                return true;
            }

            if (_spill is null || _next - 1 != storedThrough)
            {
                return false;
            }

            try
            {
                _spill.Seal(JournalSpillBound.SealedName(_spillPath, at));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            DeleteOldArchives(at - retention);
            return true;
        }
    }

    /// <summary>True when the last write succeeded or no write has been tried. A spill that is closed gets one reopen attempt.</summary>
    public bool CheckWritable()
    {
        lock (_gate)
        {
            if (_spillPath is not null && !_spillSuspended && _spill is null)
            {
                _writable = TryOpenSpill();
            }

            return _writable;
        }
    }

    /// <summary>The newest records, newest first.</summary>
    public IReadOnlyList<JournalRecord> Recent(int limit)
    {
        lock (_gate)
        {
            return _records.AsEnumerable().Reverse().Take(limit).ToArray();
        }
    }

    /// <summary>
    /// The state changes in the memory window, newest first, below <paramref name="beforeSequence"/> (or from the newest when null).
    /// Read from the <c>state</c> records, whose summary is <c>state &lt;from&gt; -&gt; &lt;to&gt;</c>; a record that does not parse is skipped.
    /// </summary>
    public IReadOnlyList<KodoStateChange> StateChanges(long? beforeSequence, int limit)
    {
        lock (_gate)
        {
            List<KodoStateChange> changes = [];
            for (int index = _records.Count - 1; index >= 0 && changes.Count < limit; index--)
            {
                JournalRecord record = _records[index];
                if (record.Kind == "state" && (beforeSequence is null || record.Sequence < beforeSequence) && ParseStateChange(record) is { } change)
                {
                    changes.Add(change);
                }
            }

            return changes;
        }
    }

    /// <summary>An inclusion proof for <paramref name="sequence"/>, or null when it is not retained or the history no longer starts at record 0.</summary>
    public (MerkleInclusionProof Proof, byte[] Root, byte[] RecordHash)? Proof(long sequence)
    {
        lock (_gate)
        {
            if (_records.Count == 0 || _records[0].Sequence != 0 || sequence < 0 || sequence >= _records.Count)
            {
                return null;
            }

            MerkleTree tree = new(_records.Select(record => record.Hash));
            MerkleInclusionProof proof = tree.InclusionProof((int)sequence, tree.Size);
            return (proof, tree.Root(), _records[(int)sequence].Hash);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _spill?.Dispose();
            _spill = null;
        }
    }

    // The monitor writes "state <from> -> <to>" with wire names; anything else is not a state change.
    private static KodoStateChange? ParseStateChange(JournalRecord record)
    {
        string text = Encoding.UTF8.GetString(record.Payload);
        string[] parts = text.Split(' ');
        return parts is ["state", { Length: > 0 } from, "->", { Length: > 0 } to]
            ? new KodoStateChange(record.Sequence, record.At, from, to)
            : null;
    }

    private void Remember(JournalRecord record)
    {
        _head = record.Hash;
        _next = record.Sequence + 1;
        _records.Add(record);
        if (_records.Count > _capacity)
        {
            _records.RemoveRange(0, _records.Count - _capacity);
        }
    }

    // A compaction at the size bound is recorded in the chain as an observation: the range that is gone, never invented data.
    private void RecordGaps(DateTimeOffset at)
    {
        for (int attempt = 0; attempt < _maxGapMarkers && _spill!.TakeGap() is { } gap; attempt++)
        {
            string summary = $"journal gap spill_bound first={gap.FirstSequence} last={gap.LastSequence} count={gap.Count}";
            Remember(_spill.Append(at, "observation", Encoding.UTF8.GetBytes(summary)));
        }
    }

    private void DeleteOldArchives(DateTimeOffset olderThan)
    {
        try
        {
            string directory = Path.GetDirectoryName(_spillPath!)!;
            foreach (string path in Directory.EnumerateFiles(directory, Path.GetFileName(_spillPath!) + ".sealed-*"))
            {
                if (JournalSpillBound.IsSealedName(_spillPath!, path) && File.GetLastWriteTimeUtc(path) < olderThan.UtcDateTime)
                {
                    File.Delete(path);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // An archive that cannot be deleted now is tried again after the next seal.
        }
    }

    // Opens the spill file and loads its tail. Called under the lock (or from the constructor).
    private bool TryOpenSpill()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_spillPath!)!);
            if (!File.Exists(_spillPath))
            {
                File.Create(_spillPath!).Dispose();
            }

            // Read before opening: the writer holds the file exclusively. Open verifies the chain and refuses a broken one.
            _spill?.Dispose();
            _spill = null;
            JournalSpillRead existing = JournalSpill.Read(_spillPath!);
            JournalSpillWriter writer = JournalSpillWriter.Open(_spillPath!, _maxSpillBytes);
            _spill = writer;
            _records.Clear();
            _records.AddRange(existing.Records.Count > _capacity ? existing.Records.Skip(existing.Records.Count - _capacity) : existing.Records);
            // A file that starts at an anchor (size bound or seal) continues the chain it came from.
            _head = existing.Records.Count > 0 ? existing.Records[^1].Hash : existing.Anchor?.PreviousHash;
            _next = existing.Records.Count > 0 ? existing.Records[^1].Sequence + 1 : existing.Anchor?.NextSequence ?? 0;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return false;
        }
    }
}
