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
    private readonly Lock _gate = new();
    private readonly string? _spillPath;
    private readonly int _capacity;
    private readonly List<JournalRecord> _records = [];
    private JournalSpillWriter? _spill;
    private byte[]? _head;
    private long _next;
    private bool _writable = true;
    private bool _spillSuspended;

    public KodoJournal(string? directory, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
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
                }
                else
                {
                    record = JournalChain.Create(_next, at, kind, payload, _head ?? JournalChain.GenesisHash());
                }

                _head = record.Hash;
                _next = record.Sequence + 1;
                _records.Add(record);
                if (_records.Count > _capacity)
                {
                    _records.RemoveRange(0, _records.Count - _capacity);
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
            JournalSpillWriter writer = JournalSpillWriter.Open(_spillPath!);
            _spill = writer;
            _records.Clear();
            _records.AddRange(existing.Records.Count > _capacity ? existing.Records.Skip(existing.Records.Count - _capacity) : existing.Records);
            _head = existing.Records.Count == 0 ? null : existing.Records[^1].Hash;
            _next = existing.Records.Count;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return false;
        }
    }
}
