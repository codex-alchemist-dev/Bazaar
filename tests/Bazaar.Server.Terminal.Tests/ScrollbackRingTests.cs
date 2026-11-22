namespace Bazaar.Server.Terminal.Tests;

public class ScrollbackRingTests
{
    private static byte[] Bytes(int n, byte fill = 1) => Enumerable.Repeat(fill, n).ToArray();

    [Fact]
    public void Capacity_below_the_floor_is_raised_to_64_KiB()
    {
        Assert.Equal(64 * 1024, new ScrollbackRing(10).Capacity);
        Assert.Equal(1024 * 1024, new ScrollbackRing(1024 * 1024).Capacity);
    }

    [Fact]
    public void Sequence_numbers_start_at_one_and_increase()
    {
        var ring = new ScrollbackRing(1000, enforceFloor: false);
        Assert.Equal(0, ring.LastSeq);
        Assert.Equal(1, ring.Append(Bytes(1)));
        Assert.Equal(2, ring.Append(Bytes(1)));
        Assert.Equal(2, ring.LastSeq);
        Assert.Equal(1, ring.OldestSeq);
    }

    [Fact]
    public void Empty_ring_replays_nothing_and_oldest_is_next_seq()
    {
        var ring = new ScrollbackRing(100, enforceFloor: false);
        Assert.Empty(ring.ReadFrom(0));
        Assert.Equal(1, ring.OldestSeq);
        Assert.False(ring.TryGet(1, out _));
    }

    [Fact]
    public void Wrap_drops_the_oldest_chunks_from_replay_only()
    {
        var ring = new ScrollbackRing(30, enforceFloor: false);
        for (var i = 0; i < 5; i++)
        {
            ring.Append(Bytes(10, (byte)(i + 1)));
        }

        // 5 chunks of 10 bytes in a 30 byte ring: seq 3, 4, 5 remain.
        Assert.Equal(5, ring.LastSeq);
        Assert.Equal(3, ring.OldestSeq);
        Assert.Equal(30, ring.ByteCount);
        Assert.Equal([3L, 4, 5], ring.ReadFrom(0).Select(c => c.Seq));
        Assert.False(ring.TryGet(2, out _));
        Assert.True(ring.TryGet(3, out var bytes));
        Assert.Equal(3, bytes.Span[0]);
    }

    [Fact]
    public void Exactly_full_ring_keeps_everything()
    {
        var ring = new ScrollbackRing(30, enforceFloor: false);
        ring.Append(Bytes(10));
        ring.Append(Bytes(10));
        ring.Append(Bytes(10));
        Assert.Equal(3, ring.ChunkCount);
        Assert.Equal(1, ring.OldestSeq);
        ring.Append(Bytes(1));
        Assert.Equal(2, ring.OldestSeq);
    }

    [Fact]
    public void Replay_from_a_sequence_returns_that_chunk_and_everything_after()
    {
        var ring = new ScrollbackRing(1000, enforceFloor: false);
        for (var i = 0; i < 6; i++)
        {
            ring.Append(Bytes(2, (byte)i));
        }

        Assert.Equal([4L, 5, 6], ring.ReadFrom(4).Select(c => c.Seq));
        Assert.Equal([1L, 2, 3, 4, 5, 6], ring.ReadFrom(0).Select(c => c.Seq));
        Assert.Empty(ring.ReadFrom(7));
        Assert.Empty(ring.ReadFrom(500));
        Assert.Equal([5L, 6], ring.ReadFrom(5, maxChunks: 2).Select(c => c.Seq));
        Assert.Equal([1L, 2], ring.ReadFrom(1, maxChunks: 2).Select(c => c.Seq));
    }

    [Fact]
    public void Replay_from_before_the_ring_starts_at_the_oldest_so_the_gap_is_visible()
    {
        var ring = new ScrollbackRing(20, enforceFloor: false);
        for (var i = 0; i < 6; i++)
        {
            ring.Append(Bytes(10));
        }

        var replay = ring.ReadFrom(1);
        Assert.Equal(5, replay[0].Seq);
        Assert.Equal(2, replay.Count);
    }

    [Fact]
    public void A_chunk_larger_than_the_ring_takes_a_sequence_but_is_not_stored_and_clears_older_data()
    {
        var ring = new ScrollbackRing(20, enforceFloor: false);
        ring.Append(Bytes(5));
        Assert.Equal(2, ring.Append(Bytes(50)));
        Assert.Equal(0, ring.ChunkCount);
        Assert.Equal(3, ring.OldestSeq);
        Assert.Empty(ring.ReadFrom(0));
        Assert.Equal(3, ring.Append(Bytes(5)));
        Assert.Equal([3L], ring.ReadFrom(0).Select(c => c.Seq));
    }

    [Fact]
    public void Append_copies_the_data()
    {
        var ring = new ScrollbackRing(100, enforceFloor: false);
        var data = Bytes(4, 7);
        ring.Append(data);
        data[0] = 99;
        ring.TryGet(1, out var stored);
        Assert.Equal(7, stored.Span[0]);
    }

    [Fact]
    public void Many_small_chunks_wrap_the_internal_array_correctly()
    {
        var ring = new ScrollbackRing(50, enforceFloor: false);
        for (var i = 1; i <= 1000; i++)
        {
            ring.Append([(byte)(i % 251)]);
        }

        Assert.Equal(50, ring.ChunkCount);
        Assert.Equal(951, ring.OldestSeq);
        var replay = ring.ReadFrom(0);
        for (var i = 0; i < replay.Count; i++)
        {
            Assert.Equal(951 + i, replay[i].Seq);
            Assert.Equal((byte)((951 + i) % 251), replay[i].Bytes.Span[0]);
        }
    }

    [Fact]
    public async Task Concurrent_appends_get_unique_consecutive_sequences()
    {
        var ring = new ScrollbackRing(1_000_000, enforceFloor: false);
        var seqs = new System.Collections.Concurrent.ConcurrentBag<long>();
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(
            () =>
            {
                for (var i = 0; i < 500; i++)
                {
                    seqs.Add(ring.Append(Bytes(3)));
                }
            },
            TestContext.Current.CancellationToken)));

        Assert.Equal(8000, seqs.Distinct().Count());
        Assert.Equal(8000, ring.LastSeq);
        Assert.Equal(Enumerable.Range(1, 8000).Select(i => (long)i), seqs.Order());
    }
}
