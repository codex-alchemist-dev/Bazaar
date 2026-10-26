using Bazaar.Contracts.Wire;
using Bazaar.Server.Wire.Sync;
using Bazaar.Testing;

namespace Bazaar.Server.Tests.Wire.Sync;

public class SubscriptionEngineTests
{
    private static void Fill(SyncHarness h, int count)
    {
        for (var i = 1; i <= count; i++)
        {
            h.Log.Append(SyncHarness.Gc, $"m{i}");
        }
    }

    [Fact]
    public async Task Replay_pages_in_order_then_goes_live_without_duplicates()
    {
        using var h = new SyncHarness();
        Fill(h, 95);
        using var c = h.Connect();

        var result = await c.SubscribeAsync();
        Assert.Equal(SubscribeStatuses.Ok, Assert.Single(result.Results).Status);

        h.Log.Append(SyncHarness.Gc, "live");
        await c.WaitAsync(() => c.Messages.Count == 96);

        var messages = c.Messages;
        Assert.Equal(Enumerable.Range(1, 96).Select(i => (long)i), messages.Select(m => m.N));
        Assert.Equal("live", messages[^1].Text);
    }

    [Fact]
    public async Task Resumes_from_the_cursor_and_skips_what_the_client_has()
    {
        using var h = new SyncHarness();
        Fill(h, 30);
        using var c = h.Connect();

        await c.SubscribeAsync(after: 25, editAfter: 25);

        Assert.Equal(new long[] { 26, 27, 28, 29, 30 }, c.Messages.Select(m => m.N));
    }

    [Fact]
    public async Task An_offline_edit_replays_once_as_the_current_version_and_n_does_not_advance()
    {
        using var h = new SyncHarness();
        Fill(h, 5);
        var target = h.Log.Snapshot(SyncHarness.Gc)[1];
        h.Log.Edit(SyncHarness.Gc, target.MessageId, "v2");
        h.Log.Edit(SyncHarness.Gc, target.MessageId, "v3");
        using var c = h.Connect();

        await c.SubscribeAsync(after: 5, editAfter: 5);

        var only = Assert.Single(c.Messages);
        Assert.Equal(target.N, only.N);
        Assert.Equal(7, only.Ev);
        Assert.Equal("v3", only.Text);
        Assert.True(only.Edited);
        Assert.Equal(5, h.Log.Snapshot(SyncHarness.Gc).Max(m => m.N));
    }

    [Fact]
    public async Task A_live_edit_arrives_with_the_original_n()
    {
        using var h = new SyncHarness();
        Fill(h, 3);
        using var c = h.Connect();
        await c.SubscribeAsync(after: 3, editAfter: 3);
        var target = h.Log.Snapshot(SyncHarness.Gc)[0];

        h.Log.Edit(SyncHarness.Gc, target.MessageId, "fixed");
        await c.WaitAsync(() => c.Messages.Count == 1);

        Assert.Equal(target.N, c.Messages[0].N);
        Assert.Equal(4, c.Messages[0].Ev);
    }

    [Fact]
    public async Task A_gap_over_the_cutoff_gets_resync_with_the_tail_then_live_events()
    {
        using var h = new SyncHarness(new SyncOptions { ReplayCutoffEntries = 20, ReplayPageSize = 10, ResyncTailSize = 4 });
        Fill(h, 30);
        using var c = h.Connect();

        var result = await c.SubscribeAsync();
        Assert.Equal(SubscribeStatuses.Resync, result.Results[0].Status);
        var resync = c.Payload<Resync>(Assert.Single(c.Of(WireTypes.Resync)));
        Assert.Equal(30, resync.LatestN);
        Assert.Equal(7, resync.Unread);
        Assert.Equal(new long[] { 27, 28, 29, 30 }, resync.Tail.Select(m => m.N));
        Assert.Empty(c.Messages);

        h.Log.Append(SyncHarness.Gc, "after");
        await c.WaitAsync(() => c.Messages.Count == 1);
        Assert.Equal(31, c.Messages[0].N);
    }

    [Fact]
    public async Task A_cursor_ahead_of_the_head_is_a_stale_cursor_and_resyncs()
    {
        using var h = new SyncHarness();
        Fill(h, 3);
        using var c = h.Connect();

        var result = await c.SubscribeAsync(after: 500, editAfter: 500);

        Assert.Equal(SubscribeStatuses.Resync, result.Results[0].Status);
        Assert.Single(c.Of(WireTypes.Resync));
    }

    [Fact]
    public async Task Non_members_missing_gcs_and_non_gc_streams_are_all_denied()
    {
        using var h = new SyncHarness();
        var stranger = Guid.NewGuid();
        using var outsider = h.Connect(stranger);
        using var insider = h.Connect();

        var denied = await outsider.SubscribeAsync();
        var missing = await insider.SubscribeAsync(gc: Guid.NewGuid());
        var outcome = await insider.Session.SubscribeAsync(
            new Subscribe([new StreamCursor(WireStreams.Presence, 0, 0)]), CancellationToken.None);

        Assert.Equal(SubscribeStatuses.Denied, denied.Results[0].Status);
        Assert.Equal(SubscribeStatuses.Denied, missing.Results[0].Status);
        Assert.Equal(SubscribeStatuses.Denied, outcome.Result.Results[0].Status);
        Assert.Empty(outsider.Session.Streams);
        Assert.Equal(0, h.Dispatcher.ActiveFeeds);
    }

    [Fact]
    public async Task A_slow_consumer_gets_lagged_and_loses_nothing_after_resubscribing_from_its_cursor()
    {
        using var h = new SyncHarness(new SyncOptions { ReplayPageSize = 10, OutboundQueueMax = 5 });
        using var c = h.Connect(paused: true);
        await c.SubscribeAsync();

        Fill(h, 20);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        c.Resume();
        await c.WaitAsync(() => c.Of(WireTypes.Lagged).Count == 1);
        var lagged = c.Payload<Lagged>(c.Of(WireTypes.Lagged)[0]);
        Assert.Equal(WireStreams.Gc(SyncHarness.Gc), lagged.S);
        await c.WaitAsync(() => c.Messages.Count == 5);
        Assert.Equal(5, lagged.LastSent);

        // Live delivery has stopped for the stream.
        h.Log.Append(SyncHarness.Gc, "ignored");
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(5, c.Messages.Count);

        var lastN = c.Messages[^1].N;
        await c.SubscribeAsync(after: lastN, editAfter: c.Messages[^1].Ev);
        await c.WaitAsync(() => c.Messages.Count == 21);
        Assert.Equal(Enumerable.Range(1, 21).Select(i => (long)i), c.Messages.Select(m => m.N));
    }

    [Fact]
    public async Task Replay_waits_for_queue_room_instead_of_lagging()
    {
        using var h = new SyncHarness(new SyncOptions { ReplayPageSize = 10, OutboundQueueMax = 5 });
        Fill(h, 60);
        using var c = h.Connect();

        await c.SubscribeAsync();

        Assert.Equal(60, c.Messages.Count);
        Assert.Empty(c.Of(WireTypes.Lagged));
    }

    [Fact]
    public async Task Revoking_access_sends_stream_revoked_and_stops_delivery()
    {
        using var h = new SyncHarness();
        Fill(h, 2);
        using var c = h.Connect();
        await c.SubscribeAsync();

        h.Log.RemoveMember(SyncHarness.Alice, SyncHarness.Gc);
        await h.Dispatcher.RecheckAccessAsync(SyncHarness.Gc, h.Log, CancellationToken.None);
        h.Log.Append(SyncHarness.Gc, "secret");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Single(c.Of(WireTypes.StreamRevoked));
        Assert.Equal(2, c.Messages.Count);
        Assert.Empty(c.Session.Streams);
        Assert.Equal(0, h.Dispatcher.ActiveFeeds);
    }

    [Fact]
    public async Task Unsubscribe_stops_delivery_and_releases_the_feed()
    {
        using var h = new SyncHarness();
        using var c = h.Connect();
        await c.SubscribeAsync();
        Assert.Equal(1, h.Dispatcher.ActiveFeeds);

        c.Session.Unsubscribe([WireStreams.Gc(SyncHarness.Gc)]);
        h.Log.Append(SyncHarness.Gc, "x");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Empty(c.Messages);
        Assert.Equal(0, h.Dispatcher.ActiveFeeds);
    }

    [Fact]
    public async Task A_lost_notify_is_recovered_by_the_reconnect_marker()
    {
        using var h = new SyncHarness();
        using var c = h.Connect();
        await c.SubscribeAsync();
        await Task.Delay(50, TestContext.Current.CancellationToken);

        h.Log.DropNotifications = true;
        Fill(h, 3);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Empty(c.Messages);

        h.Log.Reconnected();
        await c.WaitAsync(() => c.Messages.Count == 3);
    }

    [Fact]
    public async Task A_lost_notify_is_recovered_by_the_periodic_sweep()
    {
        using var h = new SyncHarness(sweep: TimeSpan.FromMilliseconds(50));
        using var c = h.Connect();
        await c.SubscribeAsync();

        h.Log.DropNotifications = true;
        Fill(h, 2);

        await c.WaitAsync(() => c.Messages.Count == 2);
    }

    [Fact]
    public async Task A_log_read_failure_in_the_pump_is_retried_without_loss()
    {
        using var h = new SyncHarness(new SyncOptions { ReplayPageSize = 10, PumpRetryDelay = TimeSpan.FromMilliseconds(10) });
        using var c = h.Connect();
        await c.SubscribeAsync();

        h.Log.FailNextReads(2);
        Fill(h, 4);

        await c.WaitAsync(() => c.Messages.Count == 4);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, c.Messages.Select(m => m.N));
    }

    [Fact]
    public async Task A_log_read_failure_during_replay_sends_lagged_so_the_client_resubscribes()
    {
        using var h = new SyncHarness();
        Fill(h, 5);
        h.Log.FailNextReads(1);
        using var c = h.Connect();

        await c.SubscribeAsync();

        Assert.Single(c.Of(WireTypes.Lagged));
        Assert.Empty(c.Session.Streams);
        await c.SubscribeAsync();
        Assert.Equal(5, c.Messages.Count);
    }

    [Fact]
    public async Task Two_connections_each_get_every_event_once()
    {
        using var h = new SyncHarness();
        using var a = h.Connect();
        using var b = h.Connect();
        await a.SubscribeAsync();
        await b.SubscribeAsync();

        Fill(h, 25);

        await a.WaitAsync(() => a.Messages.Count == 25 && b.Messages.Count == 25);
        Assert.Equal(a.Messages.Select(m => m.Ev), b.Messages.Select(m => m.Ev));
    }

    [Fact]
    public async Task Random_appends_edits_and_subscribes_converge_with_no_gap_or_duplicate()
    {
        var seed = TestSeed.FromEnvironment();
        await SeededRunAsync(seed);
    }

    private static async Task SeededRunAsync(TestSeed seed)
    {
        try
        {
            for (var round = 0; round < 15; round++)
            {
                await OneRoundAsync(new Random(unchecked((int)seed.Value + round)));
            }
        }
        catch
        {
            Console.Error.WriteLine($"Bazaar test failed: seed={seed}. Replay with {TestSeed.EnvironmentVariable}={seed}.");
            throw;
        }
    }

    private static async Task OneRoundAsync(Random rng)
    {
        using var h = new SyncHarness(new SyncOptions
        {
            ReplayPageSize = rng.Next(1, 12),
            LiveBufferMax = rng.Next(1, 20),
            ReplayCutoffEntries = 10_000,
        });
        var preload = rng.Next(0, 40);
        Fill(h, preload);

        var total = preload + rng.Next(20, 120);
        var appender = Task.Run(async () =>
        {
            for (var i = preload; i < total; i++)
            {
                var snapshot = h.Log.Snapshot(SyncHarness.Gc);
                if (snapshot.Count > 0 && rng.Next(4) == 0)
                {
                    h.Log.Edit(SyncHarness.Gc, snapshot[rng.Next(snapshot.Count)].MessageId, $"e{i}");
                }
                else
                {
                    h.Log.Append(SyncHarness.Gc, $"m{i}");
                }

                if (rng.Next(3) == 0)
                {
                    await Task.Yield();
                }
            }
        });

        using var c = h.Connect();
        for (var spins = rng.Next(0, 30); spins > 0; spins--)
        {
            await Task.Yield();
        }

        await c.SubscribeAsync();
        await appender;
        var final = h.Log.Snapshot(SyncHarness.Gc);
        var finalEv = final.Count == 0 ? 0 : final.Max(m => m.Ev);
        await c.WaitAsync(() => c.Messages.Count > 0 && c.Messages.Max(m => m.Ev) == finalEv || finalEv == 0);

        var received = c.Messages;
        Assert.Equal(received.Select(m => m.Ev).Distinct().Count(), received.Count);
        Assert.Equal(received.OrderBy(m => m.Ev).Select(m => m.Ev), received.Select(m => m.Ev));
        var lastByMessage = received.GroupBy(m => m.MessageId).ToDictionary(g => g.Key, g => g.Last());
        Assert.Equal(final.Count, lastByMessage.Count);
        foreach (var expected in final)
        {
            Assert.Equal(expected.Ev, lastByMessage[expected.MessageId].Ev);
        }
    }
}
