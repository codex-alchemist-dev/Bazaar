using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Gc.Write;
using Npgsql;

namespace Bazaar.Server.Tests.Gc;

/// <summary>G3: post-commit NOTIFY, the single listener, reconnect with resync, and replay/tail that lose nothing.</summary>
public sealed class GcListenerTests(GcDb db) : IClassFixture<GcDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan _fast = TimeSpan.FromMilliseconds(50);

    private static GcListenerOptions FastListener => new(_fast, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1));

    [Fact]
    public async Task A_committed_append_wakes_a_subscriber_through_the_notifier_and_listener()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        await using var notifier = new CoalescingGcNotifier(db.DataSource);
        await notifier.StartAsync(Ct);
        await using var listener = new GcNotifyListener(db.DataSource, FastListener);
        await listener.StartAsync(Ct);
        await listener.WaitUntilListeningAsync(Ct);
        using var sub = listener.Subscribe(gc);

        var result = await new GcLogWriter(db.DataSource, notifier).AppendAsync(GcTestData.System(gc, "a"), Ct);

        var wake = await sub.WaitAsync(TimeSpan.FromSeconds(5), TimeProvider.System, Ct);
        Assert.NotNull(wake);
        Assert.Equal(result.Seq, wake.Heads[gc]);
    }

    [Fact]
    public async Task A_rolled_back_append_sends_no_notification()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        await using var notifier = new CoalescingGcNotifier(db.DataSource);
        await notifier.StartAsync(Ct);
        await using var listener = new GcNotifyListener(db.DataSource, FastListener);
        await listener.StartAsync(Ct);
        await listener.WaitUntilListeningAsync(Ct);
        using var sub = listener.Subscribe(gc);
        var writer = new GcLogWriter(db.DataSource, notifier);

        await Assert.ThrowsAsync<PostgresException>(() =>
            writer.AppendAsync(GcTestData.System(gc, "bad") with { Kind = "not_a_registered_kind" }, Ct));

        Assert.Null(await sub.WaitAsync(TimeSpan.FromMilliseconds(500), TimeProvider.System, Ct));
        Assert.Equal(0, notifier.FlushCount);
    }

    [Fact]
    public async Task Replay_from_any_seq_equals_the_log()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var writer = new GcLogWriter(db.DataSource, new RecordingNotifier());
        for (var i = 0; i < 60; i++)
            await writer.AppendAsync(GcTestData.System(gc, $"k{i}"), Ct);
        var reader = new GcLogReader(db.DataSource, new AllowAllAccess());

        foreach (var from in new long[] { 0, 1, 17, 59, 60 })
        {
            var seen = new List<GcEvent>();
            await foreach (var e in reader.ReadAsync(gc, from, 1000, GcTestData.Viewer, Ct))
                seen.Add(e);
            Assert.Equal(Enumerable.Range((int)from + 1, 60 - (int)from).Select(i => (long)i), seen.Select(e => e.Seq));
        }
    }

    [Fact]
    public async Task Forcing_the_listener_connection_down_during_writes_loses_no_event()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        await using var notifier = new CoalescingGcNotifier(db.DataSource);
        await notifier.StartAsync(Ct);
        await using var listener = new GcNotifyListener(db.DataSource, FastListener);
        await listener.StartAsync(Ct);
        await listener.WaitUntilListeningAsync(Ct);
        var writer = new GcLogWriter(db.DataSource, notifier);
        var reader = new GcLogReader(db.DataSource, new AllowAllAccess());
        // A long sweep interval proves the hints and the reconnect resync carry delivery, not the periodic sweep.
        var tail = new GcTail(reader, listener, new GcTailOptions(PageSize: 25, SweepInterval: TimeSpan.FromMinutes(5)));

        const int total = 400;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var received = new List<long>();
        var consumer = Task.Run(async () =>
        {
            await foreach (var e in tail.FollowAsync(gc, 0, GcTestData.Viewer, cts.Token))
            {
                received.Add(e.Seq);
                if (e.Seq == total)
                    break;
            }
        }, Ct);

        var producer = Task.Run(async () =>
        {
            for (var i = 1; i <= total; i++)
            {
                await writer.AppendAsync(GcTestData.System(gc, $"k{i}"), Ct);
                if (i % 100 == 50)
                    await KillListenerBackendAsync(listener);
            }
        }, Ct);

        await producer;
        await consumer.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(Enumerable.Range(1, total).Select(i => (long)i), received);
        Assert.True(listener.ReconnectCount >= 1, $"expected at least one reconnect, saw {listener.ReconnectCount}");
    }

    [Fact]
    public async Task A_tail_with_no_listener_connection_still_delivers_through_the_sweep()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        // Listener never started: no hints at all. Only the sweep reads the log.
        await using var listener = new GcNotifyListener(db.DataSource, FastListener);
        var tail = new GcTail(new GcLogReader(db.DataSource, new AllowAllAccess()), listener, new GcTailOptions(10, TimeSpan.FromMilliseconds(100)));
        var writer = new GcLogWriter(db.DataSource, new RecordingNotifier());

        var received = new List<long>();
        var consumer = Task.Run(async () =>
        {
            await foreach (var e in tail.FollowAsync(gc, 0, GcTestData.Viewer, Ct))
            {
                received.Add(e.Seq);
                if (e.Seq == 5)
                    break;
            }
        }, Ct);
        for (var i = 1; i <= 5; i++)
            await writer.AppendAsync(GcTestData.System(gc, $"k{i}"), Ct);
        await consumer.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal([1L, 2, 3, 4, 5], received);
    }

    [Fact]
    public async Task The_listener_resyncs_subscribers_after_a_reconnect()
    {
        GcTestSkip.WithoutDatabase();
        await using var listener = new GcNotifyListener(db.DataSource, FastListener);
        await listener.StartAsync(Ct);
        await listener.WaitUntilListeningAsync(Ct);
        using var sub = listener.Subscribe();
        var pid = listener.BackendProcessId;

        await KillListenerBackendAsync(listener);

        var wake = await sub.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, Ct);
        Assert.NotNull(wake);
        Assert.True(wake.Resync);
        Assert.NotEqual(pid, listener.BackendProcessId);
        Assert.Equal(1, listener.ReconnectCount);
    }

    private async Task KillListenerBackendAsync(GcNotifyListener listener)
    {
        var pid = listener.BackendProcessId;
        if (pid == 0)
            return; // already reconnecting
        await using var cmd = db.DataSource.CreateCommand("SELECT pg_terminate_backend($1)");
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = pid });
        await cmd.ExecuteScalarAsync(Ct);
    }
}
