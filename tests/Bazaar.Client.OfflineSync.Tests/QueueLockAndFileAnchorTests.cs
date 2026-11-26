using Bazaar.Client.OfflineSync;
using static Bazaar.Client.OfflineSync.Tests.OfflineTestData;

namespace Bazaar.Client.OfflineSync.Tests;

public sealed class QueueLockAndFileAnchorTests : IDisposable
{
    private readonly TempQueueDirectory _dir = new();
    private readonly TempQueueDirectory _anchorDir = new();

    private string AnchorPath => Path.Combine(_anchorDir.Root, "queue.anchor");

    public void Dispose()
    {
        _dir.Dispose();
        _anchorDir.Dispose();
    }

    [Fact]
    public void SecondOpenOfTheSameDirectory_IsRefused_UntilTheFirstIsDisposed()
    {
        var first = new EncryptedOfflineQueue(_dir.Root, Device, Key());
        Assert.Throws<OfflineQueueLockedException>(() => new EncryptedOfflineQueue(_dir.Root, Device, Key()));

        first.Dispose();

        using var second = new EncryptedOfflineQueue(_dir.Root, Device, Key());
        Assert.Equal(1, second.NextSequence);
    }

    [Fact]
    public void FailedOpen_ReleasesTheLock()
    {
        using (var queue = new EncryptedOfflineQueue(_dir.Root, Device, Key(1)))
        {
            queue.Enqueue(Write(Device, 1));
        }

        Assert.Throws<OfflineQueueCorruptedException>(() => new EncryptedOfflineQueue(_dir.Root, Device, Key(2)));

        using var again = new EncryptedOfflineQueue(_dir.Root, Device, Key(1));
        Assert.Equal(2, again.NextSequence);
    }

    [Fact]
    public void FileAnchor_RoundTrips_AndMissingFileIsNull()
    {
        var store = new FileOfflineQueueAnchorStore(AnchorPath, Device, Key());
        Assert.Null(store.Load());

        store.Save(new OfflineQueueAnchor(7, 4));

        var reopened = new FileOfflineQueueAnchorStore(AnchorPath, Device, Key());
        Assert.Equal(new OfflineQueueAnchor(7, 4), reopened.Load());
        Assert.False(File.Exists(AnchorPath + ".tmp"));
    }

    [Fact]
    public void FileAnchor_RejectsAnEditedFile_AWrongKey_AndAnotherDevice()
    {
        var store = new FileOfflineQueueAnchorStore(AnchorPath, Device, Key());
        store.Save(new OfflineQueueAnchor(7, 4));

        Assert.Throws<OfflineQueueCorruptedException>(() => new FileOfflineQueueAnchorStore(AnchorPath, Device, Key(9)).Load());
        Assert.Throws<OfflineQueueCorruptedException>(() => new FileOfflineQueueAnchorStore(AnchorPath, OtherDevice, Key()).Load());

        var bytes = File.ReadAllBytes(AnchorPath);
        bytes[5] ^= 0xFF;
        File.WriteAllBytes(AnchorPath, bytes);
        Assert.Throws<OfflineQueueCorruptedException>(() => store.Load());

        File.WriteAllBytes(AnchorPath, bytes[..10]);
        Assert.Throws<OfflineQueueCorruptedException>(() => store.Load());
    }

    [Fact]
    public void Queue_WithFileAnchor_DetectsRemovedRecordsAcrossRestarts()
    {
        using (var queue = new EncryptedOfflineQueue(_dir.Root, Device, Key(), anchorStore: new FileOfflineQueueAnchorStore(AnchorPath, Device, Key())))
        {
            queue.Enqueue(Write(Device, 1));
            queue.Enqueue(Write(Device, 2));
            queue.Enqueue(Write(Device, 3));
        }

        File.Delete(Path.Combine(_dir.Root, EncryptedOfflineQueue.FileName));

        var ex = Assert.Throws<OfflineQueueCorruptedException>(
            () => new EncryptedOfflineQueue(_dir.Root, Device, Key(), anchorStore: new FileOfflineQueueAnchorStore(AnchorPath, Device, Key())));
        Assert.Contains("missing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Queue_WithFileAnchor_ReopensCleanly()
    {
        using (var queue = new EncryptedOfflineQueue(_dir.Root, Device, Key(), anchorStore: new FileOfflineQueueAnchorStore(AnchorPath, Device, Key())))
        {
            queue.Enqueue(Write(Device, 1));
            queue.Enqueue(Write(Device, 2));
        }

        using var reopened = new EncryptedOfflineQueue(_dir.Root, Device, Key(), anchorStore: new FileOfflineQueueAnchorStore(AnchorPath, Device, Key()));
        Assert.Equal(new long[] { 1, 2 }, reopened.PeekBatch(10).Select(w => w.DeviceSequence).ToArray());
    }
}
