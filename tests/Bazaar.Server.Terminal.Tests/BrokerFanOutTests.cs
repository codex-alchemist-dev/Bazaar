using Bazaar.Server.Terminal.Abstractions;

namespace Bazaar.Server.Terminal.Tests;

public class BrokerFanOutTests
{
    [Fact]
    public async Task Output_reaches_every_subscriber_in_order_with_sequences_from_one()
    {
        await using var h = new Harness();
        var info = await h.OpenAsync();
        await using var a = await h.SubscribeAsync(Harness.Agent1, info.TerminalId);
        await using var b = await h.SubscribeAsync(Harness.Owner, info.TerminalId);

        for (var i = 0; i < 20; i++)
        {
            h.Channel.Last.Emit("line " + i);
        }

        foreach (var sub in new[] { a, b })
        {
            var chunks = await Harness.ReadAsync(sub, 20);
            Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i), chunks.Select(c => c.Seq));
            Assert.All(chunks, c => Assert.False(c.IsReplay));
            Assert.Equal("line 7", System.Text.Encoding.UTF8.GetString(chunks[7].Bytes.Span));
        }
    }

    [Fact]
    public async Task A_late_subscriber_gets_the_ring_as_replay_then_live_output()
    {
        await using var h = new Harness();
        var info = await h.OpenAsync();
        h.Channel.Last.Emit("a");
        h.Channel.Last.Emit("b");
        await Harness.EventuallyAsync(() => h.Broker.List(Harness.Owner)[0].LastSeq == 2);

        await using var sub = await h.SubscribeAsync(Harness.Owner, info.TerminalId);
        var replay = await Harness.ReadAsync(sub, 2);
        Assert.All(replay, c => Assert.True(c.IsReplay));
        Assert.Equal([1L, 2], replay.Select(c => c.Seq));

        h.Channel.Last.Emit("c");
        var live = await Harness.ReadAsync(sub);
        Assert.Equal(3, live.Seq);
        Assert.False(live.IsReplay);
    }

    [Fact]
    public async Task Subscribing_from_a_sequence_replays_from_there()
    {
        await using var h = new Harness();
        var info = await h.OpenAsync();
        for (var i = 0; i < 5; i++)
        {
            h.Channel.Last.Emit("x" + i);
        }

        await Harness.EventuallyAsync(() => h.Broker.List(Harness.Owner)[0].LastSeq == 5);
        await using var sub = await h.SubscribeAsync(Harness.Owner, info.TerminalId, from: 4);
        Assert.Equal([4L, 5], (await Harness.ReadAsync(sub, 2)).Select(c => c.Seq));
    }

    [Fact]
    public async Task Subscribing_from_beyond_the_end_gets_only_new_output()
    {
        await using var h = new Harness();
        var info = await h.OpenAsync();
        h.Channel.Last.Emit("x");
        await Harness.EventuallyAsync(() => h.Broker.List(Harness.Owner)[0].LastSeq == 1);
        await using var sub = await h.SubscribeAsync(Harness.Owner, info.TerminalId, from: 100);
        h.Channel.Last.Emit("y");
        var chunk = await Harness.ReadAsync(sub);
        Assert.Equal(2, chunk.Seq);
    }

    [Fact]
    public async Task Output_larger_than_the_payload_limit_is_split()
    {
        await using var h = new Harness();
        var info = await h.OpenAsync();
        await using var sub = await h.SubscribeAsync(Harness.Owner, info.TerminalId);
        h.Channel.Last.Emit(new byte[40 * 1024]);

        var chunks = await Harness.ReadAsync(sub, 3);
        Assert.Equal([16384, 16384, 8192], chunks.Select(c => c.Bytes.Length));
        Assert.Equal([1L, 2, 3], chunks.Select(c => c.Seq));
    }

    [Fact]
    public async Task A_slow_subscriber_never_stalls_the_session_or_other_viewers_and_loses_nothing_the_ring_holds()
    {
        await using var h = new Harness(new TerminalOptions { SubscriberQueueFrames = 8 });
        var info = await h.OpenAsync();
        await using var slow = await h.SubscribeAsync(Harness.Owner, info.TerminalId);
        await using var fast = await h.SubscribeAsync(Harness.Agent1, info.TerminalId);

        const int total = 100;
        var fastReader = Task.Run(async () => await Harness.ReadAsync(fast, total), Harness.Ct);
        for (var i = 0; i < total; i++)
        {
            h.Channel.Last.Emit("m" + i);
            if (i % 5 == 0)
            {
                await Task.Yield();
            }
        }

        var fastChunks = await fastReader;
        Assert.Equal(Enumerable.Range(1, total).Select(i => (long)i), fastChunks.Select(c => c.Seq));

        // The session consumed everything even though "slow" read nothing.
        await Harness.EventuallyAsync(() => h.Broker.List(Harness.Owner)[0].LastSeq == total);

        var slowChunks = await Harness.ReadAsync(slow, total);
        Assert.Equal(Enumerable.Range(1, total).Select(i => (long)i), slowChunks.Select(c => c.Seq));
        Assert.Contains(slowChunks, c => c.IsReplay);
        Assert.Equal("m99", System.Text.Encoding.UTF8.GetString(slowChunks[99].Bytes.Span));
    }

    [Fact]
    public async Task A_slow_subscriber_sees_a_sequence_gap_when_the_ring_wrapped_past_it()
    {
        await using var h = new Harness(new TerminalOptions { SubscriberQueueFrames = 8, ScrollbackBytes = 64 * 1024 });
        var info = await h.OpenAsync();
        await using var slow = await h.SubscribeAsync(Harness.Owner, info.TerminalId);

        for (var i = 0; i < 20; i++)
        {
            h.Channel.Last.Emit(new byte[16 * 1024]);
        }

        await Harness.EventuallyAsync(() => h.Broker.List(Harness.Owner)[0].LastSeq == 20);

        var seqs = new List<long>();
        for (var i = 0; i < 12; i++)
        {
            seqs.Add((await Harness.ReadAsync(slow)).Seq);
        }

        // 1..8 were queued; the ring then only held 17..20.
        Assert.Equal([1L, 2, 3, 4, 5, 6, 7, 8, 17, 18, 19, 20], seqs);
    }

    [Fact]
    public async Task A_slow_subscriber_drains_even_if_the_terminal_goes_quiet()
    {
        await using var h = new Harness(new TerminalOptions { SubscriberQueueFrames = 8 });
        var info = await h.OpenAsync();
        await using var slow = await h.SubscribeAsync(Harness.Owner, info.TerminalId);
        for (var i = 0; i < 40; i++)
        {
            h.Channel.Last.Emit("q" + i);
        }

        await Harness.EventuallyAsync(() => h.Broker.List(Harness.Owner)[0].LastSeq == 40);
        var chunks = await Harness.ReadAsync(slow, 40);
        Assert.Equal(40, chunks[^1].Seq);
    }

    [Fact]
    public async Task Initial_replay_larger_than_the_queue_is_delivered_in_full_as_the_viewer_reads()
    {
        await using var h = new Harness(new TerminalOptions { SubscriberQueueFrames = 8 });
        var info = await h.OpenAsync();
        for (var i = 0; i < 50; i++)
        {
            h.Channel.Last.Emit("r" + i);
        }

        await Harness.EventuallyAsync(() => h.Broker.List(Harness.Owner)[0].LastSeq == 50);
        await using var sub = await h.SubscribeAsync(Harness.Owner, info.TerminalId);
        var chunks = await Harness.ReadAsync(sub, 50);
        Assert.Equal(Enumerable.Range(1, 50).Select(i => (long)i), chunks.Select(c => c.Seq));
        Assert.All(chunks, c => Assert.True(c.IsReplay));
    }

    [Fact]
    public async Task Disposing_a_subscription_detaches_it_and_completes_its_channel()
    {
        await using var h = new Harness();
        var info = await h.OpenAsync();
        var sub = await h.SubscribeAsync(Harness.Owner, info.TerminalId);
        await sub.DisposeAsync();
        await sub.Output.Completion.WaitAsync(TimeSpan.FromSeconds(5), Harness.Ct);
        h.Channel.Last.Emit("ignored");
        await Harness.EventuallyAsync(() => h.Broker.List(Harness.Owner)[0].LastSeq == 1);
    }

    [Fact]
    public async Task Concurrent_subscribers_each_get_a_gap_free_ordered_stream_while_output_flows()
    {
        await using var h = new Harness(new TerminalOptions { SubscriberQueueFrames = 16 });
        var info = await h.OpenAsync();
        const int total = 500;
        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(
            async () =>
            {
                await using var sub = await h.SubscribeAsync(Harness.Owner, info.TerminalId);
                var seqs = new List<long>();
                while (seqs.Count == 0 || seqs[^1] < total)
                {
                    seqs.Add((await Harness.ReadAsync(sub)).Seq);
                }

                return seqs;
            },
            Harness.Ct)).ToList();

        for (var i = 0; i < total; i++)
        {
            h.Channel.Last.Emit("z");
            if (i % 50 == 0)
            {
                await Task.Delay(1, Harness.Ct);
            }
        }

        foreach (var reader in readers)
        {
            var seqs = await reader;
            Assert.Equal(seqs.Order(), seqs);
            Assert.Equal(seqs.Distinct().Count(), seqs.Count);
            Assert.Equal(total, seqs[^1]);

            // 500 one-byte chunks fit in the ring, so nothing is lost.
            Assert.Equal(Enumerable.Range((int)seqs[0], total - (int)seqs[0] + 1).Select(i => (long)i), seqs);
        }
    }
}
