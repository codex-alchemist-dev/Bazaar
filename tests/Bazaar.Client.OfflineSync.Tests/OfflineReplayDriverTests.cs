using Bazaar.Client.OfflineSync;
using Bazaar.OfflineSync;

namespace Bazaar.Client.OfflineSync.Tests;

public sealed class OfflineReplayDriverTests : IDisposable
{
    private readonly TempQueueDirectory _dir = new();
    private readonly List<EncryptedOfflineQueue> _queues = [];

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var queue in _queues)
        {
            queue.Dispose();
        }

        _dir.Dispose();
    }

    [Fact]
    public async Task Drains_AndAcknowledgesEveryWrite()
    {
        var queue = NewQueue(directory: _dir.Root, count: 7);
        var server = new FakeOfflineServer();
        var state = new ConnectionStateTracker();

        var result = await new OfflineReplayDriver(queue, server, state, batchSize: 3).RunAsync(CancellationToken.None);

        Assert.Equal(OfflineReplayStatus.Drained, result.Status);
        Assert.Equal(7, result.AckedSequence);
        Assert.Equal(0, result.AttentionCount);
        Assert.Equal(0, queue.PendingCount);
        Assert.IsType<ConnectionState.Live>(state.Current);
    }

    [Fact]
    public async Task GapReportedByServer_IsResentFromTheGap()
    {
        var queue = NewQueue(directory: _dir.Root, count: 5);
        var server = new FakeOfflineServer { DropWriteAtSend = 1 };

        var result = await new OfflineReplayDriver(queue, server, new ConnectionStateTracker(), batchSize: 5).RunAsync(CancellationToken.None);

        Assert.Equal(OfflineReplayStatus.Drained, result.Status);
        Assert.Equal(2, server.Sends);
        Assert.Equal(5, server.AppliedLog.Count);
        Assert.Equal(5, server.AppliedLog.Distinct().Count());
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task DuplicateReplay_ChangesNothingAndCountsNoAttention()
    {
        var queue = NewQueue(directory: _dir.Root, count: 3);
        var server = new FakeOfflineServer { Acked = 3 };

        var result = await new OfflineReplayDriver(queue, server, new ConnectionStateTracker()).RunAsync(CancellationToken.None);

        Assert.Equal(OfflineReplayStatus.Drained, result.Status);
        Assert.Empty(server.AppliedLog);
        Assert.Equal(0, result.AttentionCount);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task ConflictHeldAndRejected_AreCountedIntoAttention()
    {
        var queue = NewQueue(directory: _dir.Root, count: 5);
        var server = new FakeOfflineServer();
        server.Overrides[WriteIdAt(queue, 1)] = OfflineOutcomeKind.Conflict;
        server.Overrides[WriteIdAt(queue, 3)] = OfflineOutcomeKind.HeldForReview;
        server.Overrides[WriteIdAt(queue, 4)] = OfflineOutcomeKind.Rejected;
        var state = new ConnectionStateTracker();

        var result = await new OfflineReplayDriver(queue, server, state).RunAsync(CancellationToken.None);

        Assert.Equal(OfflineReplayStatus.Drained, result.Status);
        Assert.Equal(3, result.AttentionCount);
        Assert.Equal(new ConnectionState.Attention(3), state.Current);
        Assert.Equal(2, server.AppliedLog.Count);
    }

    [Fact]
    public async Task TransportFailure_GoesOfflineAndKeepsTheQueue()
    {
        var queue = NewQueue(directory: _dir.Root, count: 4);
        var server = new FakeOfflineServer { FaultSend = 1 };
        var state = new ConnectionStateTracker();

        var result = await new OfflineReplayDriver(queue, server, state, batchSize: 2).RunAsync(CancellationToken.None);

        Assert.Equal(OfflineReplayStatus.Offline, result.Status);
        Assert.Equal(4, queue.PendingCount);
        Assert.IsType<ConnectionState.Offline>(state.Current);
    }

    [Fact]
    public async Task ServerAcknowledgingLessWithoutAGap_Stalls()
    {
        var queue = NewQueue(directory: _dir.Root, count: 3);
        var server = new FakeOfflineServer { Stale = true };
        var state = new ConnectionStateTracker();

        var result = await new OfflineReplayDriver(queue, server, state).RunAsync(CancellationToken.None);

        Assert.Equal(OfflineReplayStatus.Stalled, result.Status);
        Assert.Equal(1, result.AttentionCount);
        Assert.Equal(new ConnectionState.Attention(1), state.Current);
        Assert.Equal(3, queue.PendingCount);
    }

    [Fact]
    public async Task CorruptedQueue_StallsWithAttention()
    {
        var queue = NewQueue(directory: _dir.Root, count: 2);
        var file = Path.Combine(_dir.Root, EncryptedOfflineQueue.FileName);
        var bytes = File.ReadAllBytes(file);
        bytes[^1] ^= 0x01;
        File.WriteAllBytes(file, bytes);
        var state = new ConnectionStateTracker();

        var result = await new OfflineReplayDriver(queue, new FakeOfflineServer(), state).RunAsync(CancellationToken.None);

        Assert.Equal(OfflineReplayStatus.Stalled, result.Status);
        Assert.Equal(1, result.AttentionCount);
        Assert.True(queue.IsStopped);
    }

    [Theory]
    [MemberData(nameof(ChaosCases))]
    public async Task Chaos_FailingOneSend_EndsWithEveryWriteAppliedOnce(int failingSend, bool faultAfterApply)
    {
        const int total = 10;
        var queue = NewQueue(directory: _dir.Root, count: total);
        var server = new FakeOfflineServer { FaultSend = failingSend, FaultAfterApply = faultAfterApply };
        var driver = new OfflineReplayDriver(queue, server, new ConnectionStateTracker(), batchSize: 3);

        OfflineReplayResult? last = null;
        for (var attempt = 0; attempt < 20 && last?.Status != OfflineReplayStatus.Drained; attempt++)
        {
            last = await driver.RunAsync(CancellationToken.None);
            Assert.NotEqual(OfflineReplayStatus.Stalled, last.Status);
        }

        Assert.Equal(OfflineReplayStatus.Drained, last?.Status);
        Assert.Equal(0, queue.PendingCount);
        Assert.Equal(total, server.Acked);
        Assert.Equal(total, server.AppliedLog.Count);
        Assert.Equal(total, server.AppliedLog.Distinct().Count());
    }

    public static IEnumerable<object[]> ChaosCases()
    {
        for (var k = 1; k <= 10; k++)
        {
            yield return new object[] { k, false };
            yield return new object[] { k, true };
        }
    }

    [Fact]
    public async Task RepairedTornTail_CountsTheLostWriteIntoAttention()
    {
        var anchors = new InMemoryOfflineQueueAnchorStore();
        using (var first = new EncryptedOfflineQueue(_dir.Root, OfflineTestData.Device, OfflineTestData.Key(), anchorStore: anchors))
        {
            for (var sequence = 1; sequence <= 3; sequence++)
            {
                first.Enqueue(OfflineTestData.Write(OfflineTestData.Device, sequence, "write " + sequence));
            }
        }

        var file = Path.Combine(_dir.Root, EncryptedOfflineQueue.FileName);
        var bytes = File.ReadAllBytes(file);
        File.WriteAllBytes(file, bytes[..^5]);

        var queue = new EncryptedOfflineQueue(
            _dir.Root,
            OfflineTestData.Device,
            OfflineTestData.Key(),
            repairMode: OfflineQueueRepairMode.QuarantineTornTail,
            anchorStore: anchors);
        _queues.Add(queue);
        var server = new FakeOfflineServer();
        server.Overrides[WriteIdAt(queue, 1)] = OfflineOutcomeKind.Conflict;
        var state = new ConnectionStateTracker();

        var result = await new OfflineReplayDriver(queue, server, state).RunAsync(CancellationToken.None);

        Assert.Equal(OfflineReplayStatus.Drained, result.Status);
        Assert.Equal(1, result.RepairedLostWrites);
        Assert.Equal(2, result.AttentionCount);
        Assert.Equal(new ConnectionState.Attention(2), state.Current);
        Assert.Single(server.AppliedLog);
    }

    [Fact]
    public async Task CleanQueue_ReportsNoRepairedWrites()
    {
        var queue = NewQueue(directory: _dir.Root, count: 2);

        var result = await new OfflineReplayDriver(queue, new FakeOfflineServer(), new ConnectionStateTracker()).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.RepairedLostWrites);
        Assert.Equal(0, result.AttentionCount);
    }

    private EncryptedOfflineQueue NewQueue(string directory, int count)
    {
        var queue = new EncryptedOfflineQueue(directory, OfflineTestData.Device, OfflineTestData.Key());
        _queues.Add(queue);
        for (var sequence = 1; sequence <= count; sequence++)
        {
            queue.Enqueue(OfflineTestData.Write(OfflineTestData.Device, sequence, "write " + sequence));
        }

        return queue;
    }

    private static Guid WriteIdAt(EncryptedOfflineQueue queue, long sequence) =>
        queue.PeekBatch((int)sequence)[(int)sequence - 1].WriteId;
}
