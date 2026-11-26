using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using Bazaar.OfflineSync;

namespace Bazaar.Client.OfflineSync;

/// <summary>
/// The client's pending-write queue (doc 56 section 6): one AES-256-GCM record per write, appended in device sequence order.
/// </summary>
/// <remarks>
/// File layout. Header: "BOQ1", then the device sequence of the first record (int64 LE). For an empty file that value is the next
/// sequence to be written. Then, per record: int32 LE body length, a 12-byte nonce, the ciphertext, and a 16-byte tag.
/// The associated data of each record is the DeviceId bytes followed by the DeviceSequence (int64 LE). The sequence is not stored
/// in the record; it follows from position. A swapped, reordered or shifted record therefore fails authentication.
/// Any verification failure stops the queue: every later call throws <see cref="OfflineQueueCorruptedException"/> and nothing is skipped.
/// Removing whole records from the end of the file is not detectable by the format alone. An <see cref="IOfflineQueueAnchorStore"/>
/// kept outside the file (doc 56 section 6, open item 7) closes that gap: each append is fsynced before the anchor records it, and
/// open fails when the file holds fewer records than the anchor allows.
/// With <see cref="OfflineQueueRepairMode.QuarantineTornTail"/>, a torn or unauthenticated final record is quarantined and the queue
/// is rebuilt from the valid prefix, reporting the lost write in <see cref="Repair"/>. Any other failure still stops the queue.
/// Within one process all members are thread safe. A second open of the same queue directory, in this or another process, throws <see cref="OfflineQueueLockedException"/> until the first is disposed.
/// </remarks>
[SuppressMessage("Naming", "CA1711", Justification = "The name is fixed by the doc 56 client queue contract.")]
public sealed class EncryptedOfflineQueue : IDisposable
{
    /// <summary>The queue file name inside the queue directory.</summary>
    public const string FileName = "offline-queue.bin";

    private const int _keyBytes = 32;
    private const int _nonceBytes = 12;
    private const int _tagBytes = 16;
    private const int _headerBytes = 12;
    private const int _lengthBytes = 4;
    private const int _maxPlaintextBytes = 16 * 1024 * 1024;
    private const int _maxBodyBytes = _nonceBytes + _tagBytes + _maxPlaintextBytes;
    private const int _associatedDataBytes = 16 + 8;

    private static ReadOnlySpan<byte> Magic => "BOQ1"u8;

    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly Guid _deviceId;
    private readonly OfflineQueueLimits _limits;
    private readonly AesGcm _aes;
    private readonly IOfflineQueueAnchorStore? _anchors;
    private OfflineQueueDirectoryLock? _directoryLock;
    private readonly OfflineQueueRepairMode _repairMode;
    private long _base;
    private long _next;
    private long _count;
    private long _bytes;
    private long _acked;
    private RepairReport? _repair;
    private OfflineQueueCorruptedException? _fault;
    private bool _disposed;

    private delegate bool RecordVisitor(OfflineWrite write, byte[] frame);

    private readonly record struct ScanResult(long First, long Count, long Bytes, bool TornTail);

    /// <summary>Opens the queue in <paramref name="directory"/>, creating it when absent, and verifies every record.</summary>
    /// <param name="directory">The directory that holds the queue file.</param>
    /// <param name="deviceId">The device whose writes this queue holds.</param>
    /// <param name="keyProvider">Supplies the 32-byte key.</param>
    /// <param name="limits">Size limits; null for the defaults.</param>
    /// <param name="repairMode">What to do with a torn final record. The default fails closed.</param>
    /// <param name="anchorStore">Where the anchor lives, outside the file. Null disables anchor checks (and torn-tail repair).</param>
    /// <exception cref="OfflineQueueCorruptedException">A record failed verification, or the file disagrees with the anchor.</exception>
    public EncryptedOfflineQueue(
        string directory,
        Guid deviceId,
        IOfflineQueueKeyProvider keyProvider,
        OfflineQueueLimits? limits = null,
        OfflineQueueRepairMode repairMode = OfflineQueueRepairMode.FailClosed,
        IOfflineQueueAnchorStore? anchorStore = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(keyProvider);

        _limits = limits ?? OfflineQueueLimits.Default;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaxWrites);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_limits.MaxBytes);

        var key = keyProvider.GetKey();
        if (key.Length != _keyBytes)
        {
            throw new ArgumentException($"The queue key must be {_keyBytes} bytes.", nameof(keyProvider));
        }

        _deviceId = deviceId;
        _repairMode = repairMode;
        _anchors = anchorStore;
        _aes = new AesGcm(key.Span, _tagBytes);
        try
        {
            Directory.CreateDirectory(directory);
            _directoryLock = OfflineQueueDirectoryLock.Acquire(directory);
            _path = Path.Combine(directory, FileName);
            var anchor = _anchors?.Load();
            if (!File.Exists(_path))
            {
                if (anchor is { RecordCount: > 0 })
                {
                    throw Corrupt($"the queue file is missing but the anchor records {anchor.RecordCount} writes");
                }

                CreateEmpty(_path);
            }

            var tolerateTornTail = _repairMode == OfflineQueueRepairMode.QuarantineTornTail && _anchors is not null;
            var scan = Scan(visit: null, maxRecords: int.MaxValue, tolerateTornTail);
            var lastValid = scan.First + scan.Count - 1;
            _base = scan.First;
            _count = scan.Count;
            _bytes = scan.Bytes;
            _next = lastValid + 1;
            _acked = _base - 1;

            var lost = VerifyAgainstAnchor(anchor, lastValid, scan.TornTail);
            if (scan.TornTail)
            {
                RepairTornTail(scan, lost);
            }

            if (_anchors is not null && !Equals(anchor, new OfflineQueueAnchor(_next - 1, _acked)))
            {
                SaveAnchor();
            }
        }
        catch
        {
            _directoryLock?.Dispose();
            _aes.Dispose();
            throw;
        }
    }

    /// <summary>The device this queue holds writes for.</summary>
    public Guid DeviceId => _deviceId;

    /// <summary>The repair made when the queue was opened, or null when none was needed. The driver raises Attention for a repair.</summary>
    public RepairReport? Repair => _repair;

    /// <summary>Whether the queue has stopped after a verification failure.</summary>
    public bool IsStopped
    {
        get
        {
            lock (_gate)
            {
                return _fault is not null;
            }
        }
    }

    /// <summary>The device sequence the next write must carry (1 for a new queue).</summary>
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

    /// <summary>The number of pending (unacknowledged) writes.</summary>
    public long PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>
    /// Appends a write. The caller supplies the device sequence; it must equal <see cref="NextSequence"/>.
    /// Returns <see cref="EnqueueStatus.Full"/> with a reason, and writes nothing, when a limit is reached.
    /// </summary>
    /// <exception cref="InvalidOperationException">The sequence is a gap or a repeat.</exception>
    /// <exception cref="ArgumentException">The write belongs to another device or is too large.</exception>
    public EnqueueResult Enqueue(OfflineWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        lock (_gate)
        {
            ThrowIfUnusable();
            if (write.DeviceId != _deviceId)
            {
                throw new ArgumentException("The write belongs to another device than this queue.", nameof(write));
            }

            if (write.DeviceSequence != _next)
            {
                throw new InvalidOperationException(
                    $"Device sequence {write.DeviceSequence} breaks the gapless order; the queue expects {_next} (sequences start at 1 and have no gaps).");
            }

            var plaintext = OfflineWriteFraming.Encode(write);
            if (plaintext.Length > _maxPlaintextBytes)
            {
                throw new ArgumentException("The write exceeds the queue record size limit.", nameof(write));
            }

            if (_count >= _limits.MaxWrites)
            {
                return EnqueueResult.Full($"The offline queue holds {_count} writes, its limit. Reconnect to send them before editing more offline.");
            }

            var frame = Seal(write.DeviceSequence, plaintext);
            if (_bytes + frame.Length > _limits.MaxBytes)
            {
                return EnqueueResult.Full($"The offline queue is at its {_limits.MaxBytes} byte limit. Reconnect to send the pending writes before editing more offline.");
            }

            using (var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                stream.Write(frame);
                stream.Flush(flushToDisk: true);
            }

            _count++;
            _bytes += frame.Length;
            _next++;
            SaveAnchor();
            return EnqueueResult.Accepted;
        }
    }

    /// <summary>Returns up to <paramref name="maxCount"/> pending writes from the front, in sequence order, verified.</summary>
    /// <exception cref="OfflineQueueCorruptedException">A record failed verification; the queue is now stopped.</exception>
    public IReadOnlyList<OfflineWrite> PeekBatch(int maxCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);
        lock (_gate)
        {
            ThrowIfUnusable();
            var batch = new List<OfflineWrite>(Math.Min(maxCount, 256));
            try
            {
                Scan((write, _) =>
                {
                    batch.Add(write);
                    return batch.Count < maxCount;
                }, maxCount);
            }
            catch (OfflineQueueCorruptedException ex)
            {
                _fault = ex;
                throw;
            }

            return batch;
        }
    }

    /// <summary>
    /// Removes every pending write with a sequence up to and including <paramref name="upToSequence"/>. Rewrites the file
    /// through a temporary file and an atomic move. Acknowledging an already removed sequence does nothing.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The sequence has not been enqueued yet.</exception>
    /// <exception cref="OfflineQueueCorruptedException">A record failed verification; the queue is now stopped.</exception>
    public void Acknowledge(long upToSequence)
    {
        lock (_gate)
        {
            ThrowIfUnusable();
            if (upToSequence >= _next)
            {
                throw new ArgumentOutOfRangeException(nameof(upToSequence), upToSequence, $"Cannot acknowledge {upToSequence}; the queue has only reached {_next - 1}.");
            }

            if (upToSequence < _base)
            {
                return;
            }

            try
            {
                Compact(upToSequence);
                _acked = upToSequence;
                SaveAnchor();
            }
            catch (OfflineQueueCorruptedException ex)
            {
                _fault = ex;
                throw;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _aes.Dispose();
            _directoryLock?.Dispose();
        }
    }

    /// <summary>Checks the file against the anchor and returns the writes lost to a torn tail (0 or 1).</summary>
    private static long VerifyAgainstAnchor(OfflineQueueAnchor? anchor, long lastValid, bool tornTail)
    {
        if (anchor is null)
        {
            if (tornTail)
            {
                throw Corrupt("a torn tail cannot be told apart from truncation without an anchor");
            }

            return 0;
        }

        // The anchor is saved after each fsynced append, so it never runs ahead of the file by more than the torn record.
        var allowed = lastValid + (tornTail ? 1 : 0);
        if (anchor.RecordCount > allowed)
        {
            throw Corrupt($"the file holds {lastValid} records but the anchor expects {anchor.RecordCount}; records were removed");
        }

        if (anchor.AckedSequence > lastValid)
        {
            throw Corrupt($"the file ends at {lastValid} but the anchor acknowledges {anchor.AckedSequence}");
        }

        return Math.Max(0, anchor.RecordCount - lastValid);
    }

    /// <summary>
    /// Keeps a copy of the whole damaged file under a quarantine name, then truncates the queue file to its valid prefix. The
    /// copy is made first so a crash never loses the original bytes.
    /// </summary>
    private void RepairTornTail(ScanResult scan, long lostWrites)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var quarantine = $"{_path}.quarantine-{stamp}";
        for (var attempt = 2; File.Exists(quarantine); attempt++)
        {
            quarantine = $"{_path}.quarantine-{stamp}-{attempt.ToString(CultureInfo.InvariantCulture)}";
        }

        using (var source = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var copy = new FileStream(quarantine, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            source.CopyTo(copy);
            copy.Flush(flushToDisk: true);
        }

        using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(_headerBytes + scan.Bytes);
            stream.Flush(flushToDisk: true);
        }

        _repair = new RepairReport(lostWrites, quarantine);
    }

    private void SaveAnchor() => _anchors?.Save(new OfflineQueueAnchor(_next - 1, _acked));

    private static void CreateEmpty(string path)
    {
        var temp = path + ".creating";
        File.WriteAllBytes(temp, Header(1));
        File.Move(temp, path, overwrite: true);
    }

    private static byte[] Header(long firstSequence)
    {
        var header = new byte[_headerBytes];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(4), firstSequence);
        return header;
    }

    private static void WriteAssociatedData(Guid deviceId, long sequence, Span<byte> destination)
    {
        deviceId.ToByteArray().CopyTo(destination);
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..], sequence);
    }

    private static OfflineQueueCorruptedException Corrupt(string reason) =>
        new($"Offline queue corrupted: {reason}. The queue is stopped; it does not skip records.");

    private void ThrowIfUnusable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_fault is not null)
        {
            throw new OfflineQueueCorruptedException($"The offline queue is stopped: {_fault.Message}", _fault);
        }
    }

    private byte[] Seal(long sequence, ReadOnlySpan<byte> plaintext)
    {
        var frame = new byte[_lengthBytes + _nonceBytes + plaintext.Length + _tagBytes];
        BinaryPrimitives.WriteInt32LittleEndian(frame, _nonceBytes + plaintext.Length + _tagBytes);
        var nonce = frame.AsSpan(_lengthBytes, _nonceBytes);
        var cipher = frame.AsSpan(_lengthBytes + _nonceBytes, plaintext.Length);
        var tag = frame.AsSpan(_lengthBytes + _nonceBytes + plaintext.Length, _tagBytes);

        RandomNumberGenerator.Fill(nonce);
        Span<byte> associated = stackalloc byte[_associatedDataBytes];
        WriteAssociatedData(_deviceId, sequence, associated);
        _aes.Encrypt(nonce, plaintext, cipher, tag, associated);
        return frame;
    }

    private bool TryDecrypt(long sequence, ReadOnlySpan<byte> body, out byte[] plaintext)
    {
        var nonce = body[.._nonceBytes];
        var cipher = body[_nonceBytes..^_tagBytes];
        var tag = body[^_tagBytes..];
        plaintext = new byte[cipher.Length];

        Span<byte> associated = stackalloc byte[_associatedDataBytes];
        WriteAssociatedData(_deviceId, sequence, associated);
        try
        {
            _aes.Decrypt(nonce, cipher, tag, plaintext, associated);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private OfflineWrite Open(long sequence, ReadOnlySpan<byte> body)
    {
        if (!TryDecrypt(sequence, body, out var plaintext))
        {
            throw Corrupt($"record {sequence} failed authentication");
        }

        OfflineWrite write;
        try
        {
            write = OfflineWriteFraming.Decode(plaintext);
        }
        catch (FormatException)
        {
            throw Corrupt($"record {sequence} has an invalid payload");
        }

        if (write.DeviceId != _deviceId || write.DeviceSequence != sequence)
        {
            throw Corrupt($"record {sequence} does not match its position");
        }

        return write;
    }

    /// <summary>Reads and verifies records from the start of the file, calling <paramref name="visit"/> for each.</summary>
    private ScanResult Scan(RecordVisitor? visit, int maxRecords, bool tolerateTornTail = false)
    {
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);

        Span<byte> header = stackalloc byte[_headerBytes];
        if (ReadFully(stream, header) != _headerBytes || !header[..4].SequenceEqual(Magic))
        {
            throw Corrupt("the queue header is missing or invalid");
        }

        var first = BinaryPrimitives.ReadInt64LittleEndian(header[4..]);
        if (first < 1)
        {
            throw Corrupt("the queue header sequence is invalid");
        }

        Span<byte> prefix = stackalloc byte[_lengthBytes];
        var sequence = first;
        long count = 0;
        long bytes = 0;
        var tornTail = false;
        while (count < maxRecords)
        {
            var read = ReadFully(stream, prefix);
            if (read == 0)
            {
                break;
            }

            if (read < _lengthBytes)
            {
                if (tolerateTornTail)
                {
                    tornTail = true;
                    break;
                }

                throw Corrupt($"record {sequence} is truncated");
            }

            var bodyLength = BinaryPrimitives.ReadInt32LittleEndian(prefix);
            if (bodyLength < _nonceBytes + _tagBytes || bodyLength > _maxBodyBytes)
            {
                throw Corrupt($"record {sequence} has an invalid length");
            }

            var frame = new byte[_lengthBytes + bodyLength];
            prefix.CopyTo(frame);
            if (ReadFully(stream, frame.AsSpan(_lengthBytes)) != bodyLength)
            {
                if (tolerateTornTail)
                {
                    tornTail = true;
                    break;
                }

                throw Corrupt($"record {sequence} is truncated");
            }

            // Only the final record may be forgiven: it must end exactly at the end of the file and fail authentication.
            if (tolerateTornTail && stream.Position == stream.Length && !TryDecrypt(sequence, frame.AsSpan(_lengthBytes), out _))
            {
                tornTail = true;
                break;
            }

            var write = Open(sequence, frame.AsSpan(_lengthBytes));
            count++;
            bytes += frame.Length;
            if (visit is not null && !visit(write, frame))
            {
                break;
            }

            sequence++;
        }

        return new ScanResult(first, count, bytes, tornTail);
    }

    /// <summary>Rewrites the file without the records up to <paramref name="upToSequence"/>. Every record is verified on the way.</summary>
    private void Compact(long upToSequence)
    {
        var temp = _path + ".compacting";
        long keptCount = 0;
        long keptBytes = 0;
        try
        {
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                output.Write(Header(upToSequence + 1));
                Scan((write, frame) =>
                {
                    if (write.DeviceSequence > upToSequence)
                    {
                        output.Write(frame);
                        keptCount++;
                        keptBytes += frame.Length;
                    }

                    return true;
                }, int.MaxValue);
                output.Flush(flushToDisk: true);
            }

            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }

            throw;
        }

        _base = upToSequence + 1;
        _count = keptCount;
        _bytes = keptBytes;
    }

    private static int ReadFully(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
