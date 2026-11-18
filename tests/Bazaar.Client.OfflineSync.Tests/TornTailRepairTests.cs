using System.Buffers.Binary;
using Bazaar.Client.OfflineSync;
using Bazaar.OfflineSync;

namespace Bazaar.Client.OfflineSync.Tests;

public sealed class TornTailRepairTests : IDisposable
{
    private readonly TempQueueDirectory _dir = new();
    private readonly InMemoryOfflineQueueAnchorStore _anchors = new();

    private string FilePath => Path.Combine(_dir.Root, EncryptedOfflineQueue.FileName);

    /// <inheritdoc />
    public void Dispose() => _dir.Dispose();

    [Fact]
    public void TornFinalRecord_IsRepaired_AndReportsTheLostWrite()
    {
        AppendThree();
        TruncateBy(5);

        using var queue = Open(OfflineQueueRepairMode.QuarantineTornTail);

        var repair = Assert.IsType<RepairReport>(queue.Repair);
        Assert.Equal(1, repair.LostWrites);
        Assert.Equal(new long[] { 1, 2 }, queue.PeekBatch(10).Select(w => w.DeviceSequence).ToArray());
        Assert.Equal(3, queue.NextSequence);
        Assert.False(queue.IsStopped);
    }

    [Fact]
    public void TornRecordNeverAnchored_LosesNothing()
    {
        using (var queue = Open(OfflineQueueRepairMode.FailClosed))
        {
            queue.Enqueue(Write(1));
            queue.Enqueue(Write(2));
            queue.Enqueue(Write(3));
        }

        // As if the crash came after the append but before the third anchor was saved.
        TruncateBy(5);
        _anchors.Save(new OfflineQueueAnchor(2, 0));

        using var repaired = Open(OfflineQueueRepairMode.QuarantineTornTail);
        Assert.Equal(0, Assert.IsType<RepairReport>(repaired.Repair).LostWrites);
        Assert.Equal(2, repaired.PendingCount);
    }

    [Fact]
    public void TornFinalRecord_FailsClosed_InTheDefaultMode()
    {
        AppendThree();
        TruncateBy(5);
        var damaged = File.ReadAllBytes(FilePath);

        Assert.Throws<OfflineQueueCorruptedException>(() => Open(OfflineQueueRepairMode.FailClosed).Dispose());

        Assert.Equal(damaged, File.ReadAllBytes(FilePath));
        Assert.Empty(Directory.GetFiles(_dir.Root, "*.quarantine-*"));
    }

    [Fact]
    public void TornFinalRecord_WithoutAnAnchor_FailsClosedEvenInRepairMode()
    {
        using (var queue = new EncryptedOfflineQueue(_dir.Root, OfflineTestData.Device, OfflineTestData.Key()))
        {
            queue.Enqueue(Write(1));
            queue.Enqueue(Write(2));
        }

        TruncateBy(5);

        Assert.Throws<OfflineQueueCorruptedException>(() => new EncryptedOfflineQueue(
            _dir.Root,
            OfflineTestData.Device,
            OfflineTestData.Key(),
            null,
            OfflineQueueRepairMode.QuarantineTornTail).Dispose());
    }

    [Fact]
    public void MidFileTamper_IsNotRepaired()
    {
        AppendThree();
        var bytes = File.ReadAllBytes(FilePath);
        var secondRecord = RecordStarts(bytes)[1];
        bytes[secondRecord + 4 + 12 + 1] ^= 0x01;
        File.WriteAllBytes(FilePath, bytes);

        Assert.Throws<OfflineQueueCorruptedException>(() => Open(OfflineQueueRepairMode.QuarantineTornTail).Dispose());

        Assert.Equal(bytes, File.ReadAllBytes(FilePath));
        Assert.Empty(Directory.GetFiles(_dir.Root, "*.quarantine-*"));
    }

    [Fact]
    public void WholeTailRecordRemoved_IsDetectedThroughTheAnchor_InBothModes()
    {
        AppendThree();
        var file = File.ReadAllBytes(FilePath);
        var truncated = file[..RecordStarts(file)[2]];
        File.WriteAllBytes(FilePath, truncated);

        Assert.Throws<OfflineQueueCorruptedException>(() => Open(OfflineQueueRepairMode.FailClosed).Dispose());
        Assert.Throws<OfflineQueueCorruptedException>(() => Open(OfflineQueueRepairMode.QuarantineTornTail).Dispose());

        Assert.Equal(truncated, File.ReadAllBytes(FilePath));
        Assert.Empty(Directory.GetFiles(_dir.Root, "*.quarantine-*"));
    }

    [Fact]
    public void DeletedQueueFile_IsDetectedThroughTheAnchor()
    {
        AppendThree();
        File.Delete(FilePath);

        Assert.Throws<OfflineQueueCorruptedException>(() => Open(OfflineQueueRepairMode.QuarantineTornTail).Dispose());

        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Repair_KeepsTheDamagedFile_AndRestartContinuesTheSequence()
    {
        AppendThree();
        TruncateBy(5);
        var damaged = File.ReadAllBytes(FilePath);

        using (var repaired = Open(OfflineQueueRepairMode.QuarantineTornTail))
        {
            var repair = Assert.IsType<RepairReport>(repaired.Repair);
            Assert.StartsWith(FilePath + ".quarantine-", repair.QuarantinePath, StringComparison.Ordinal);
            Assert.Equal(damaged, File.ReadAllBytes(repair.QuarantinePath));
            Assert.Equal(EnqueueStatus.Accepted, repaired.Enqueue(Write(3, "redo")).Status);
        }

        using var reopened = Open(OfflineQueueRepairMode.QuarantineTornTail);
        Assert.Null(reopened.Repair);
        Assert.Equal(new long[] { 1, 2, 3 }, reopened.PeekBatch(10).Select(w => w.DeviceSequence).ToArray());
        Assert.Equal(4, reopened.NextSequence);
    }

    [Fact]
    public void Anchor_TracksAppendsAndAcknowledgements()
    {
        using var queue = Open(OfflineQueueRepairMode.FailClosed);
        queue.Enqueue(Write(1));
        queue.Enqueue(Write(2));
        Assert.Equal(new OfflineQueueAnchor(2, 0), _anchors.Load());

        queue.Acknowledge(1);
        Assert.Equal(new OfflineQueueAnchor(2, 1), _anchors.Load());
    }

    [Fact]
    public void CleanQueue_InRepairMode_ReportsNoRepair()
    {
        AppendThree();

        using var queue = Open(OfflineQueueRepairMode.QuarantineTornTail);

        Assert.Null(queue.Repair);
        Assert.Equal(3, queue.PendingCount);
    }

    private EncryptedOfflineQueue Open(OfflineQueueRepairMode mode) =>
        new(_dir.Root, OfflineTestData.Device, OfflineTestData.Key(), null, mode, _anchors);

    private void AppendThree()
    {
        using var queue = Open(OfflineQueueRepairMode.FailClosed);
        for (var sequence = 1; sequence <= 3; sequence++)
        {
            queue.Enqueue(Write(sequence));
        }
    }

    private void TruncateBy(int bytes)
    {
        var file = File.ReadAllBytes(FilePath);
        File.WriteAllBytes(FilePath, file[..^bytes]);
    }

    private static OfflineWrite Write(long sequence, string? payload = null) =>
        OfflineTestData.Write(OfflineTestData.Device, sequence, payload ?? "record " + sequence);

    private static List<int> RecordStarts(byte[] file)
    {
        var starts = new List<int>();
        var offset = 12;
        while (offset < file.Length)
        {
            starts.Add(offset);
            offset += 4 + BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(offset));
        }

        return starts;
    }
}
