using Bazaar.Diagnostics.Capture;

namespace Bazaar.Diagnostics.Tests.Capture;

public class RingBufferTests
{
    [Fact]
    public void Zero_capacity_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RingBuffer<int>(0));
    }

    [Fact]
    public void Items_under_capacity_are_kept_in_order_with_no_drops()
    {
        var buffer = new RingBuffer<int>(3);

        buffer.Add(1);
        buffer.Add(2);

        Assert.Equal(2, buffer.Count);
        Assert.Equal(0, buffer.DroppedCount);
        Assert.Equal([1, 2], buffer.Snapshot());
    }

    [Fact]
    public void Full_buffer_drops_the_oldest_and_counts_each_drop()
    {
        var buffer = new RingBuffer<int>(3);

        for (var i = 1; i <= 5; i++)
        {
            buffer.Add(i);
        }

        Assert.Equal(3, buffer.Capacity);
        Assert.Equal(3, buffer.Count);
        Assert.Equal(2, buffer.DroppedCount);
        Assert.Equal([3, 4, 5], buffer.Snapshot());
    }

    [Fact]
    public void Snapshot_is_a_copy_that_later_adds_do_not_change()
    {
        var buffer = new RingBuffer<string>(2);
        buffer.Add("a");
        var before = buffer.Snapshot();

        buffer.Add("b");
        buffer.Add("c");

        Assert.Equal(["a"], before);
        Assert.Equal(["b", "c"], buffer.Snapshot());
    }

    [Fact]
    public void Concurrent_adds_keep_capacity_and_count_every_drop()
    {
        const int threads = 8;
        const int perThread = 1000;
        const int capacity = 50;
        var buffer = new RingBuffer<int>(capacity);

        Parallel.For(0, threads, _ =>
        {
            for (var i = 0; i < perThread; i++)
            {
                buffer.Add(i);
            }
        });

        Assert.Equal(capacity, buffer.Count);
        Assert.Equal((long)threads * perThread - capacity, buffer.DroppedCount);
        Assert.Equal(capacity, buffer.Snapshot().Count);
    }
}
