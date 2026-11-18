using System.Buffers.Binary;
using System.Text;
using Bazaar.Client.OfflineSync;
using Bazaar.OfflineSync;

namespace Bazaar.Client.OfflineSync.Tests;

public sealed class EncryptedOfflineQueueTests : IDisposable
{
    private readonly TempQueueDirectory _dir = new();

    private string FilePath => Path.Combine(_dir.Root, EncryptedOfflineQueue.FileName);

    /// <inheritdoc />
    public void Dispose() => _dir.Dispose();

    [Fact]
    public void RoundTrip_ReturnsTheSameWritesAfterReopen()
    {
        var sent = new[] { OfflineTestData.Write(OfflineTestData.Device, 1, "alpha"), OfflineTestData.Write(OfflineTestData.Device, 2, "beta") };
        using (var queue = Open())
        {
            foreach (var write in sent)
            {
                Assert.Equal(EnqueueStatus.Accepted, queue.Enqueue(write).Status);
            }
        }

        using var reopened = Open();
        var batch = reopened.PeekBatch(10);
        Assert.Equal(sent.Length, batch.Count);
        for (var i = 0; i < sent.Length; i++)
        {
            OfflineTestData.AssertSameWrite(sent[i], batch[i]);
        }
    }

    [Fact]
    public void FileBytes_DoNotContainThePlaintextPayload()
    {
        const string marker = "SECRET-GC-TEXT-7f3a";
        using (var queue = Open())
        {
            queue.Enqueue(OfflineTestData.Write(OfflineTestData.Device, 1, marker));
        }

        var bytes = File.ReadAllBytes(FilePath);
        Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(marker)));
    }

    [Fact]
    public void FlippingAnyByte_IsDetectedOnOpen()
    {
        WriteTwoRecords();
        var original = File.ReadAllBytes(FilePath);

        for (var i = 0; i < original.Length; i++)
        {
            var damaged = (byte[])original.Clone();
            damaged[i] ^= 0x01;
            File.WriteAllBytes(FilePath, damaged);
            Assert.Throws<OfflineQueueCorruptedException>(OpenAndClose);
        }
    }

    [Fact]
    public void SwappedRecords_AreDetectedOnOpen()
    {
        WriteTwoRecords();
        var file = File.ReadAllBytes(FilePath);
        var records = SplitRecords(file);

        var swapped = file[..12].Concat(records[1]).Concat(records[0]).ToArray();
        File.WriteAllBytes(FilePath, swapped);

        Assert.Throws<OfflineQueueCorruptedException>(OpenAndClose);
    }

    [Fact]
    public void TruncatedRecord_IsDetectedOnOpen()
    {
        WriteTwoRecords();
        var file = File.ReadAllBytes(FilePath);

        var boundaries = new HashSet<int> { 12 };
        var offset = 12;
        foreach (var record in SplitRecords(file))
        {
            offset += record.Length;
            boundaries.Add(offset);
        }

        for (var cut = 0; cut < file.Length; cut++)
        {
            if (boundaries.Contains(cut))
            {
                continue;
            }

            File.WriteAllBytes(FilePath, file[..cut]);
            Assert.Throws<OfflineQueueCorruptedException>(OpenAndClose);
        }
    }

    [Fact]
    public void RemovingWholeTailRecord_IsNotDetectedByTheFormatAlone()
    {
        // Known limit: removing a whole record at the end leaves a valid file. Detecting that needs a count kept outside the file.
        WriteTwoRecords();
        var file = File.ReadAllBytes(FilePath);
        var records = SplitRecords(file);
        File.WriteAllBytes(FilePath, file[..(file.Length - records[1].Length)]);

        using var queue = Open();
        Assert.Equal(1, queue.PendingCount);
    }

    [Fact]
    public void WrongKey_IsRejectedOnOpen()
    {
        WriteTwoRecords();
        Assert.Throws<OfflineQueueCorruptedException>(() => OpenWithKey(9));
    }

    [Fact]
    public void KeyOfWrongLength_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            using var queue = new EncryptedOfflineQueue(_dir.Root, OfflineTestData.Device, new ShortKeyProvider());
        });
    }

    [Fact]
    public void RestartRecovery_KeepsUnackedWritesAndTheSequence()
    {
        using (var queue = Open())
        {
            for (var sequence = 1; sequence <= 3; sequence++)
            {
                queue.Enqueue(OfflineTestData.Write(OfflineTestData.Device, sequence));
            }

            queue.Acknowledge(1);
        }

        using var reopened = Open();
        var batch = reopened.PeekBatch(10);
        Assert.Equal(new long[] { 2, 3 }, batch.Select(w => w.DeviceSequence).ToArray());
        Assert.Equal(4, reopened.NextSequence);
        Assert.Equal(EnqueueStatus.Accepted, reopened.Enqueue(OfflineTestData.Write(OfflineTestData.Device, 4)).Status);
    }

    [Fact]
    public void Enqueue_RejectsGapsRepeatsAndOtherDevices()
    {
        using var queue = Open();
        Assert.Throws<InvalidOperationException>(() =>
        {
            queue.Enqueue(OfflineTestData.Write(OfflineTestData.Device, 2));
        });

        queue.Enqueue(OfflineTestData.Write(OfflineTestData.Device, 1));
        Assert.Throws<InvalidOperationException>(() =>
        {
            queue.Enqueue(OfflineTestData.Write(OfflineTestData.Device, 1));
        });
        Assert.Throws<ArgumentException>(() =>
        {
            queue.Enqueue(OfflineTestData.Write(OfflineTestData.OtherDevice, 2));
        });

        Assert.Equal(2, queue.NextSequence);
        Assert.Equal(1, queue.PendingCount);
    }

    [Fact]
    public void WriteLimit_ReturnsFullWithReason_AndWritesNothing()
    {
        using var queue = Open(new OfflineQueueLimits(MaxWrites: 2));
        Assert.Equal(EnqueueStatus.Accepted, queue.Enqueue(OfflineTestData.Write(OfflineTestData.Device, 1)).Status);
        Assert.Equal(EnqueueStatus.Accepted, queue.Enqueue(OfflineTestData.Write(OfflineTestData.Device, 2)).Status);

        var result = queue.Enqueue(OfflineTestData.Write(OfflineTestData.Device, 3));

        Assert.Equal(EnqueueStatus.Full, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        Assert.Equal(2, queue.PendingCount);
        Assert.Equal(3, queue.NextSequence);
    }

    [Fact]
    public void ByteLimit_ReturnsFullWithReason()
    {
        using var queue = Open(new OfflineQueueLimits(MaxBytes: 64));

        var result = queue.Enqueue(OfflineTestData.Write(OfflineTestData.Device, 1, new string('x', 200)));

        Assert.Equal(EnqueueStatus.Full, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void Acknowledge_RemovesAckedWritesAndKeepsTheRest()
    {
        using var queue = Open();
        for (var sequence = 1; sequence <= 5; sequence++)
        {
            queue.Enqueue(OfflineTestData.Write(OfflineTestData.Device, sequence));
        }

        queue.Acknowledge(3);
        queue.Acknowledge(3);
        Assert.Equal(new long[] { 4, 5 }, queue.PeekBatch(10).Select(w => w.DeviceSequence).ToArray());
        Assert.Equal(2, queue.PendingCount);

        queue.Acknowledge(5);
        Assert.Empty(queue.PeekBatch(10));
        Assert.Equal(6, queue.NextSequence);
        Assert.Equal(EnqueueStatus.Accepted, queue.Enqueue(OfflineTestData.Write(OfflineTestData.Device, 6)).Status);
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.Acknowledge(7));
    }

    [Fact]
    public void AfterCorruptionDuringCompaction_TheQueueStaysStopped_AndTheFileIsUntouched()
    {
        using var queue = Open();
        queue.Enqueue(OfflineTestData.Write(OfflineTestData.Device, 1));
        queue.Enqueue(OfflineTestData.Write(OfflineTestData.Device, 2));

        var tampered = File.ReadAllBytes(FilePath);
        tampered[^1] ^= 0x40;
        File.WriteAllBytes(FilePath, tampered);

        Assert.Throws<OfflineQueueCorruptedException>(() => queue.Acknowledge(1));
        Assert.True(queue.IsStopped);
        Assert.Throws<OfflineQueueCorruptedException>(() =>
        {
            queue.PeekBatch(1);
        });
        Assert.Throws<OfflineQueueCorruptedException>(() =>
        {
            queue.Enqueue(OfflineTestData.Write(OfflineTestData.Device, 3));
        });
        Assert.Equal(tampered, File.ReadAllBytes(FilePath));
        Assert.Single(Directory.GetFiles(_dir.Root));
    }

    private EncryptedOfflineQueue Open(OfflineQueueLimits? limits = null) =>
        new(_dir.Root, OfflineTestData.Device, OfflineTestData.Key(), limits);

    private void OpenAndClose()
    {
        using var queue = Open();
    }

    private void OpenWithKey(byte seed)
    {
        using var queue = new EncryptedOfflineQueue(_dir.Root, OfflineTestData.Device, OfflineTestData.Key(seed));
    }

    private void WriteTwoRecords()
    {
        using var queue = Open();
        queue.Enqueue(OfflineTestData.Write(OfflineTestData.Device, 1, "first record"));
        queue.Enqueue(OfflineTestData.Write(OfflineTestData.Device, 2, "second record"));
    }

    private static List<byte[]> SplitRecords(byte[] file)
    {
        var records = new List<byte[]>();
        var offset = 12;
        while (offset < file.Length)
        {
            var length = 4 + BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(offset));
            records.Add(file[offset..(offset + length)]);
            offset += length;
        }

        return records;
    }

    private sealed class ShortKeyProvider : IOfflineQueueKeyProvider
    {
        public ReadOnlyMemory<byte> GetKey() => new byte[16];
    }
}
