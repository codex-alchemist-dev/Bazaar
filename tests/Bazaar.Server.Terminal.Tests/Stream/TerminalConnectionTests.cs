using System.Text;
using Bazaar.Contracts.Terminal;
using Bazaar.Server.Terminal.Abstractions;
using Bazaar.Server.Terminal.Stream;

namespace Bazaar.Server.Terminal.Tests.Stream;

public class TerminalConnectionTests
{
    [Fact]
    public async Task Open_creates_a_terminal_replies_opened_and_streams_output()
    {
        await using var h = new StreamHarness();
        await h.SendAsync(new TerminalOpen("req", null, 80, 24));

        var opened = await h.NextAsync<TerminalOpened>();
        Assert.NotEqual("req", opened.TerminalId);
        Assert.True(opened.WriterHeld);
        Assert.Equal(1, opened.ReplayFrom);
        Assert.Equal(1, h.Connection.SubscriptionCount);

        h.Inner.Channel.Last.Emit("hello");
        var output = await h.NextAsync<TerminalOutput>();
        Assert.Equal(opened.TerminalId, output.TerminalId);
        Assert.Equal(1, output.Seq);
        Assert.Equal("hello", StreamHarness.Text(output));
    }

    [Fact]
    public async Task Open_of_an_unresolvable_target_is_a_not_found_error_and_creates_nothing()
    {
        await using var h = new StreamHarness();
        h.Resolver.Refuse = true;
        await h.SendAsync(new TerminalOpen("req", "agent-9", 80, 24));

        var error = await h.NextAsync<TerminalError>();
        Assert.Equal("terminal.not_found", error.Code);
        Assert.Empty(h.Inner.Channel.Ptys);
    }

    [Fact]
    public async Task Open_with_a_bad_size_maps_the_broker_refusal()
    {
        await using var h = new StreamHarness();
        await h.SendAsync(new TerminalOpen("req", null, 0, 24));
        var error = await h.NextAsync<TerminalError>();
        Assert.Equal(TerminalFrameMapper.ErrorCode(TerminalRefusal.InvalidSize), error.Code);
    }

    [Fact]
    public async Task Open_with_no_backend_fails_closed()
    {
        await using var h = new StreamHarness();
        h.Inner.Channel.IsAvailable = false;
        await h.SendAsync(new TerminalOpen("req", null, 80, 24));
        Assert.Equal("terminal.no_backend", (await h.NextAsync<TerminalError>()).Code);
    }

    [Fact]
    public async Task Open_of_an_existing_visible_terminal_attaches_and_reports_the_replay_origin()
    {
        await using var h = new StreamHarness(Harness.Owner);
        var info = await h.Inner.OpenAsync();
        h.Inner.Channel.Last.Emit("a");
        h.Inner.Channel.Last.Emit("b");
        await Harness.EventuallyAsync(() => h.Inner.Broker.List(Harness.Owner)[0].LastSeq == 2);

        await h.SendAsync(new TerminalOpen(info.TerminalId, "agent-1", 80, 24));
        var opened = await h.NextAsync<TerminalOpened>();
        Assert.Equal(info.TerminalId, opened.TerminalId);
        Assert.Equal(1, opened.ReplayFrom);
        Assert.Single(h.Inner.Channel.Ptys);
        Assert.Equal(1, (await h.NextAsync<TerminalOutput>()).Seq);
        Assert.Equal(2, (await h.NextAsync<TerminalOutput>()).Seq);
    }

    [Fact]
    public async Task Input_and_resize_reach_the_guest_without_a_reply()
    {
        await using var h = new StreamHarness();
        var id = await h.OpenTerminalAsync();
        await h.SendAsync(new TerminalInput(id, Encoding.UTF8.GetBytes("ls\n")));
        await h.SendAsync(new TerminalResize(id, 100, 30));

        Assert.Equal("ls\n", Encoding.UTF8.GetString(h.Inner.Channel.Last.AllWritten));
        Assert.Equal([(100, 30)], h.Inner.Channel.Last.Resizes);
        Assert.Empty(h.Drain());
    }

    [Fact]
    public async Task Input_without_control_is_a_forbidden_error_frame()
    {
        await using var h = new StreamHarness(Harness.Owner);
        var info = await h.Inner.OpenAsync();
        await h.Inner.Broker.ReleaseControlAsync(Harness.Agent1, info.TerminalId, Harness.Ct);

        await h.SendAsync(new TerminalInput(info.TerminalId, [1]));
        Assert.Equal("terminal.forbidden", (await h.NextAsync<TerminalError>()).Code);
        Assert.Empty(h.Inner.Channel.Last.Writes);
    }

    [Fact]
    public async Task Control_take_release_reply_granted_and_denied()
    {
        await using var h = new StreamHarness(Harness.Owner);
        var info = await h.Inner.OpenAsync();

        await h.SendAsync(new TerminalControl(info.TerminalId, "take"));
        Assert.Equal("denied", (await h.NextAsync<TerminalControl>()).Action);

        await h.Inner.Broker.ReleaseControlAsync(Harness.Agent1, info.TerminalId, Harness.Ct);
        await h.SendAsync(new TerminalControl(info.TerminalId, "take"));
        Assert.Equal("granted", (await h.NextAsync<TerminalControl>()).Action);

        await h.SendAsync(new TerminalControl(info.TerminalId, "release"));
        Assert.Equal("granted", (await h.NextAsync<TerminalControl>()).Action);
        Assert.Null(h.Inner.Broker.List(Harness.Owner)[0].WriterMemberId);
    }

    [Fact]
    public async Task Unknown_control_action_is_a_bad_request()
    {
        await using var h = new StreamHarness();
        var id = await h.OpenTerminalAsync();
        await h.SendAsync(new TerminalControl(id, "grab"));
        Assert.Equal(TerminalConnection.CodeBadRequest, (await h.NextAsync<TerminalError>()).Code);
    }

    [Fact]
    public async Task Replay_resubscribes_from_the_sequence_and_does_not_duplicate_live_frames()
    {
        await using var h = new StreamHarness();
        var id = await h.OpenTerminalAsync();
        for (var i = 0; i < 5; i++)
        {
            h.Inner.Channel.Last.Emit("x" + i);
        }

        for (var i = 1; i <= 5; i++)
        {
            Assert.Equal(i, (await h.NextAsync<TerminalOutput>()).Seq);
        }

        await h.SendAsync(new TerminalReplay(id, 4));
        Assert.Equal(4, (await h.NextAsync<TerminalOutput>()).Seq);
        Assert.Equal(5, (await h.NextAsync<TerminalOutput>()).Seq);
        Assert.Equal(1, h.Connection.SubscriptionCount);

        h.Inner.Channel.Last.Emit("live");
        var live = await h.NextAsync<TerminalOutput>();
        Assert.Equal(6, live.Seq);
        Assert.Empty(h.Drain());
        await Harness.EventuallyAsync(() => h.Broker.Live == 1);
    }

    [Fact]
    public async Task Replay_of_a_hidden_terminal_is_forbidden()
    {
        await using var h = new StreamHarness(Harness.Agent2);
        var info = await h.Inner.OpenAsync();
        await h.SendAsync(new TerminalReplay(info.TerminalId, 0));
        Assert.Equal("terminal.forbidden", (await h.NextAsync<TerminalError>()).Code);
        Assert.Equal(0, h.Connection.SubscriptionCount);
    }

    [Fact]
    public async Task Close_ends_the_terminal_and_the_pump_delivers_the_exit()
    {
        await using var h = new StreamHarness();
        var id = await h.OpenTerminalAsync();
        await h.SendAsync(new TerminalClose(id));

        var exit = await h.NextAsync<TerminalExit>();
        Assert.Equal(id, exit.TerminalId);
        await Harness.EventuallyAsync(() => h.Connection.SubscriptionCount == 0 && h.Broker.Live == 0);
        Assert.Empty(h.Inner.Broker.List(Harness.Owner));
    }

    [Fact]
    public async Task Close_by_a_viewer_who_cannot_write_just_detaches()
    {
        await using var h = new StreamHarness(Harness.Human);
        h.Inner.Grants.View.Add("human");
        var info = await h.Inner.OpenAsync();
        await h.SendAsync(new TerminalReplay(info.TerminalId, 0));
        await Harness.EventuallyAsync(() => h.Connection.SubscriptionCount == 1);

        await h.SendAsync(new TerminalClose(info.TerminalId));
        Assert.Equal(0, h.Connection.SubscriptionCount);
        Assert.Equal(0, h.Broker.Live);
        Assert.Single(h.Inner.Broker.List(Harness.Owner));
        Assert.Empty(h.Drain());
    }

    [Fact]
    public async Task Exit_propagates_after_all_output_and_drops_the_subscription()
    {
        await using var h = new StreamHarness();
        var id = await h.OpenTerminalAsync();
        h.Inner.Channel.Last.Emit("bye");
        h.Inner.Channel.Last.Exit(7);

        Assert.Equal(1, (await h.NextAsync<TerminalOutput>()).Seq);
        var exit = await h.NextAsync<TerminalExit>();
        Assert.Equal(id, exit.TerminalId);
        Assert.Equal(7, exit.Code);
        await Harness.EventuallyAsync(() => h.Connection.SubscriptionCount == 0 && h.Broker.Live == 0);
    }

    [Fact]
    public async Task Another_callers_terminal_is_forbidden_on_every_operation()
    {
        await using var h = new StreamHarness(Harness.Agent2);
        var victim = await h.Inner.OpenAsync(Harness.Agent1);
        var id = victim.TerminalId;
        TerminalFrame[] attempts =
        [
            new TerminalOpen(id, "agent-1", 80, 24),
            new TerminalReplay(id, 0),
            new TerminalInput(id, [65]),
            new TerminalResize(id, 10, 10),
            new TerminalControl(id, "take"),
            new TerminalControl(id, "release"),
            new TerminalClose(id),
        ];

        foreach (var attempt in attempts)
        {
            await h.SendAsync(attempt);
            var error = await h.NextAsync<TerminalError>();
            Assert.Equal("terminal.forbidden", error.Code);
            Assert.Equal(id, error.TerminalId);
        }

        Assert.Empty(h.Inner.Channel.Last.Writes);
        Assert.Empty(h.Inner.Channel.Last.Resizes);
        Assert.Equal(0, h.Broker.Total);
        Assert.Single(h.Inner.Broker.List(Harness.Owner));
        Assert.Equal("agent-1", h.Inner.Broker.List(Harness.Owner)[0].WriterMemberId);
    }

    [Fact]
    public async Task A_frame_cannot_claim_another_identity()
    {
        // The wire frames have no caller field; an extra one must be ignored, not trusted.
        await using var h = new StreamHarness(Harness.Agent2);
        var victim = await h.Inner.OpenAsync(Harness.Agent1);
        await h.SendRawAsync($$"""{"type":"input","terminalId":"{{victim.TerminalId}}","bytes":"QQ==","memberId":"agent-1","isOwner":true}""");
        Assert.Equal("terminal.forbidden", (await h.NextAsync<TerminalError>()).Code);
        Assert.Empty(h.Inner.Channel.Last.Writes);
    }

    [Fact]
    public async Task Server_to_client_frames_are_rejected_from_a_client()
    {
        await using var h = new StreamHarness();
        await h.SendAsync(new TerminalOutput("t1", 1, [1]));
        await h.SendAsync(new TerminalExit("t1", 0));
        await h.SendAsync(new TerminalOpened("t1", true, 1));
        await h.SendAsync(new TerminalError("t1", "x", "y"));
        var frames = h.Drain();
        Assert.Equal(4, frames.Count);
        Assert.All(frames, f => Assert.Equal(TerminalConnection.CodeBadRequest, Assert.IsType<TerminalError>(f).Code));
    }

    [Fact]
    public async Task Malformed_frames_become_error_frames_with_stable_codes_and_never_throw()
    {
        await using var h = new StreamHarness();
        await h.SendRawAsync("not json");
        await h.SendRawAsync("[1]");
        await h.SendRawAsync("""{"terminalId":"t1"}""");
        await h.SendRawAsync("""{"type":"bogus","terminalId":"t1"}""");
        await h.SendRawAsync("""{"type":"input","terminalId":"bad id!","bytes":"QQ=="}""");
        await h.SendRawAsync("""{"type":"input","terminalId":"t1"}""");
        await h.Connection.HandleInboundAsync(ReadOnlyMemory<byte>.Empty, StreamHarness.Ct);

        var errors = h.Drain().Cast<TerminalError>().ToList();
        Assert.Equal(7, errors.Count);
        Assert.Equal(TerminalConnection.CodeMalformed, errors[0].Code);
        Assert.Equal(TerminalConnection.CodeMalformed, errors[1].Code);
        Assert.Equal(TerminalConnection.CodeBadRequest, errors[2].Code);
        Assert.Equal(TerminalConnection.CodeBadRequest, errors[3].Code);
        Assert.Equal("t1", errors[3].TerminalId);
        Assert.Equal(TerminalConnection.ConnectionScopeId, errors[4].TerminalId);
        Assert.Equal("t1", errors[5].TerminalId);
        Assert.False(h.Connection.IsClosed);
    }

    [Fact]
    public async Task Oversize_frames_are_refused()
    {
        await using var h = new StreamHarness();
        var big = new byte[70 * 1024];
        Array.Fill(big, (byte)' ');
        await h.Connection.HandleInboundAsync(big, StreamHarness.Ct);
        Assert.Equal(TerminalConnection.CodeOversize, (await h.NextAsync<TerminalError>()).Code);

        var payload = Convert.ToBase64String(new byte[20 * 1024]);
        await h.SendRawAsync($$"""{"type":"input","terminalId":"t1","bytes":"{{payload}}"}""");
        Assert.Equal(TerminalConnection.CodeOversize, (await h.NextAsync<TerminalError>()).Code);
    }

    [Fact]
    public async Task Thirty_two_malformed_frames_in_a_row_close_the_connection()
    {
        await using var h = new StreamHarness();
        var id = await h.OpenTerminalAsync();
        for (var i = 0; i < 31; i++)
        {
            await h.SendRawAsync("garbage");
        }

        Assert.False(h.Connection.IsClosed);
        await h.SendRawAsync("garbage");
        Assert.True(h.Connection.IsClosed);
        await h.Connection.Closed.WaitAsync(TimeSpan.FromSeconds(10), StreamHarness.Ct);

        var frames = h.Drain().OfType<TerminalError>().ToList();
        Assert.Equal(32, frames.Count(f => f.Code == TerminalConnection.CodeMalformed));
        Assert.Equal(TerminalConnection.CodeTooManyMalformed, frames[^1].Code);
        Assert.True(h.Connection.Outbound.Completion.IsCompleted);
        Assert.Equal(0, h.Broker.Live);
        Assert.Equal(0, h.Connection.SubscriptionCount);

        // Closed means closed: later frames do nothing.
        await h.SendAsync(new TerminalInput(id, [1]));
        Assert.Empty(h.Inner.Channel.Last.Writes);
    }

    [Fact]
    public async Task A_valid_frame_resets_the_malformed_budget()
    {
        await using var h = new StreamHarness();
        var id = await h.OpenTerminalAsync();
        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < 31; i++)
            {
                await h.SendRawAsync("garbage");
            }

            await h.SendAsync(new TerminalResize(id, 90 + round, 30));
        }

        Assert.False(h.Connection.IsClosed);
        Assert.Equal(3, h.Inner.Channel.Last.Resizes.Count);
    }

    [Fact]
    public async Task The_malformed_budget_can_be_tightened_but_not_loosened()
    {
        await using var tight = new StreamHarness(options: new TerminalConnectionOptions { MaxConsecutiveMalformed = 3 });
        for (var i = 0; i < 3; i++)
        {
            await tight.SendRawAsync("x");
        }

        Assert.True(tight.Connection.IsClosed);

        await using var loose = new StreamHarness(options: new TerminalConnectionOptions { MaxConsecutiveMalformed = 1000 });
        for (var i = 0; i < 32; i++)
        {
            await loose.SendRawAsync("x");
        }

        Assert.True(loose.Connection.IsClosed);
    }

    [Fact]
    public async Task Overflow_drops_the_oldest_output_only_and_keeps_the_opened_frame()
    {
        await using var h = new StreamHarness(options: new TerminalConnectionOptions { OutboundQueueFrames = 8 });
        var id = await h.OpenTerminalAsync();
        _ = id;

        // Nobody reads: 50 outputs against a queue of 8.
        h.Inner.Channel.Last.Emit("warm");
        await Harness.EventuallyAsync(() => h.Connection.Outbound.Count == 1);
        for (var i = 2; i <= 50; i++)
        {
            h.Inner.Channel.Last.Emit("o" + i);
        }

        await Harness.EventuallyAsync(() => h.Inner.Broker.List(Harness.Owner)[0].LastSeq == 50);
        await Harness.EventuallyAsync(() => h.Connection.DroppedOutputFrames >= 42);

        var frames = h.Drain().Cast<TerminalOutput>().ToList();
        Assert.Equal(8, frames.Count);
        Assert.Equal(50, frames[^1].Seq);
        Assert.Equal(frames.Select(f => f.Seq).OrderBy(s => s), frames.Select(f => f.Seq));
        Assert.Equal(43, frames[0].Seq);
        Assert.Equal(1, h.Connection.SubscriptionCount);
    }

    [Fact]
    public async Task A_gap_is_recoverable_with_replay_and_nothing_special_precedes_output()
    {
        await using var h = new StreamHarness(options: new TerminalConnectionOptions { OutboundQueueFrames = 8 });
        var id = await h.OpenTerminalAsync();
        for (var i = 1; i <= 30; i++)
        {
            h.Inner.Channel.Last.Emit("o" + i);
        }

        await Harness.EventuallyAsync(() => h.Connection.DroppedOutputFrames >= 22);
        var first = await h.NextAsync<TerminalOutput>();
        Assert.True(first.Seq > 1, "the oldest frames were dropped");

        // The client sees the gap, drains, and asks for the missing range.
        h.Drain();
        await h.SendAsync(new TerminalReplay(id, 25));
        var replayed = await h.NextAsync<TerminalOutput>();
        Assert.Equal(25, replayed.Seq);
    }

    [Fact]
    public async Task Control_and_error_frames_are_never_dropped_even_when_output_floods_the_queue()
    {
        await using var h = new StreamHarness(Harness.Owner, new TerminalConnectionOptions { OutboundQueueFrames = 8 });
        var info = await h.Inner.OpenAsync();
        await h.SendAsync(new TerminalReplay(info.TerminalId, 0));
        await Harness.EventuallyAsync(() => h.Connection.SubscriptionCount == 1);
        for (var i = 0; i < 40; i++)
        {
            h.Inner.Channel.Last.Emit("o" + i);
        }

        await Harness.EventuallyAsync(() => h.Connection.DroppedOutputFrames > 0 && h.Connection.Outbound.Count == 8);

        await h.SendAsync(new TerminalControl(info.TerminalId, "take"));
        for (var i = 0; i < 5; i++)
        {
            await h.SendRawAsync("bad");
        }

        await h.SendAsync(new TerminalInput(info.TerminalId, [1]));
        var frames = h.Drain();
        Assert.Contains(frames, f => f is TerminalControl { Action: "denied" });
        Assert.Equal(5, frames.OfType<TerminalError>().Count(e => e.Code == TerminalConnection.CodeMalformed));
        Assert.Contains(frames, f => f is TerminalError { Code: "terminal.forbidden" });
    }

    [Fact]
    public async Task A_peer_that_never_reads_cannot_grow_the_queue_without_bound()
    {
        await using var h = new StreamHarness(options: new TerminalConnectionOptions { OutboundQueueFrames = 8, MaxConsecutiveMalformed = 32 });
        var id = await h.OpenTerminalAsync();
        h.Drain();
        for (var i = 0; i < 200 && !h.Connection.IsClosed; i++)
        {
            await h.SendAsync(new TerminalControl(id, "grab"));
        }

        Assert.True(h.Connection.IsClosed);
        await h.Connection.Closed.WaitAsync(TimeSpan.FromSeconds(10), StreamHarness.Ct);
        Assert.True(h.Connection.Outbound.Count <= 8 * 4);
        Assert.Equal(0, h.Broker.Live);
    }

    [Fact]
    public async Task Dispose_unsubscribes_everything_and_leaves_no_running_pumps()
    {
        var h = new StreamHarness(Harness.Owner);
        var a = await h.Inner.OpenAsync();
        var b = await h.Inner.OpenAsync();
        await h.SendAsync(new TerminalReplay(a.TerminalId, 0));
        await h.SendAsync(new TerminalReplay(b.TerminalId, 0));
        await Harness.EventuallyAsync(() => h.Broker.Live == 2);

        await h.Connection.DisposeAsync();
        Assert.Equal(0, h.Broker.Live);
        Assert.Equal(0, h.Connection.SubscriptionCount);
        Assert.True(h.Connection.Closed.IsCompleted);
        Assert.True(h.Connection.IsClosed);

        // Output after disposal goes nowhere.
        h.Inner.Channel.Ptys[0].Emit("late");
        await Task.Delay(50, StreamHarness.Ct);
        Assert.False(h.Connection.Outbound.TryRead(out _));
        await h.Connection.Outbound.Completion.WaitAsync(TimeSpan.FromSeconds(10), StreamHarness.Ct);

        await h.Connection.DisposeAsync();
        await h.Inner.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_drains_what_was_already_queued_then_completes()
    {
        var h = new StreamHarness();
        var id = await h.OpenTerminalAsync();
        await h.SendAsync(new TerminalControl(id, "grab"));
        await h.Connection.DisposeAsync();

        var error = await h.NextAsync<TerminalError>();
        Assert.Equal(TerminalConnection.CodeBadRequest, error.Code);
        Assert.False(await h.Connection.Outbound.WaitToReadAsync(StreamHarness.Ct));
        await h.Inner.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_while_a_replay_is_in_flight_does_not_hang_or_leak()
    {
        var h = new StreamHarness();
        var id = await h.OpenTerminalAsync();
        var emitter = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                h.Inner.Channel.Last.Emit("x" + i);
            }
        }, StreamHarness.Ct);

        var replays = Task.Run(async () =>
        {
            for (var i = 0; i < 20; i++)
            {
                await h.SendAsync(new TerminalReplay(id, 1));
            }
        }, StreamHarness.Ct);

        await Task.Delay(5, StreamHarness.Ct);
        await h.Connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), StreamHarness.Ct);
        await emitter;
        await replays.WaitAsync(TimeSpan.FromSeconds(10), StreamHarness.Ct);
        await Harness.EventuallyAsync(() => h.Broker.Live == 0);
        await h.Inner.DisposeAsync();
    }

    [Fact]
    public async Task Concurrent_inbound_frames_are_all_handled_in_order_per_caller_of_the_broker()
    {
        await using var h = new StreamHarness();
        var id = await h.OpenTerminalAsync();
        var tasks = Enumerable.Range(0, 100)
            .Select(i => Task.Run(async () => await h.SendAsync(new TerminalInput(id, [(byte)'a'])), StreamHarness.Ct))
            .ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(100, h.Inner.Channel.Last.AllWritten.Length);
        Assert.Empty(h.Drain());
    }

    [Fact]
    public async Task Concurrent_opens_and_replays_keep_one_pump_per_terminal()
    {
        await using var h = new StreamHarness(Harness.Owner);
        var info = await h.Inner.OpenAsync();
        var tasks = Enumerable.Range(0, 40)
            .Select(i => Task.Run(async () =>
            {
                TerminalFrame frame = i % 2 == 0 ? new TerminalOpen(info.TerminalId, "agent-1", 80, 24) : new TerminalReplay(info.TerminalId, 0);
                await h.SendAsync(frame);
            }, StreamHarness.Ct))
            .ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(1, h.Connection.SubscriptionCount);
        await Harness.EventuallyAsync(() => h.Broker.Live == 1);
        Assert.Single(h.Inner.Channel.Ptys);
    }

    [Fact]
    public async Task Concurrent_input_and_output_and_garbage_do_not_corrupt_state()
    {
        await using var h = new StreamHarness();
        var id = await h.OpenTerminalAsync();
        var drain = Task.Run(async () =>
        {
            // The reader may be slower than the emitter, so frames can be dropped; order and the final frame must hold.
            var seqs = new List<long>();
            while (seqs.Count == 0 || seqs[^1] < 300)
            {
                var frame = await h.NextAsync();
                if (frame is TerminalOutput o)
                {
                    seqs.Add(o.Seq);
                }
            }

            return seqs;
        }, StreamHarness.Ct);

        var emit = Task.Run(() =>
        {
            for (var i = 0; i < 300; i++)
            {
                h.Inner.Channel.Last.Emit("e" + i);
            }
        }, StreamHarness.Ct);
        var garbage = Task.Run(async () =>
        {
            for (var i = 0; i < 20; i++)
            {
                await h.SendRawAsync("junk");
                await h.SendAsync(new TerminalResize(id, 80, 24));
            }
        }, StreamHarness.Ct);

        await Task.WhenAll(emit, garbage);
        var seqs = await drain.WaitAsync(TimeSpan.FromSeconds(20), StreamHarness.Ct);
        Assert.Equal(seqs.OrderBy(x => x).Distinct(), seqs);
        Assert.Equal(300, seqs[^1]);
    }

    [Fact]
    public async Task Visible_terminals_lists_only_what_the_caller_may_see()
    {
        await using var h = new StreamHarness(Harness.Agent1);
        await h.Inner.OpenAsync(Harness.Agent1);
        await h.Inner.OpenAsync(Harness.Agent2, "agent-2");

        var visible = h.Connection.VisibleTerminals();
        Assert.Single(visible);
        Assert.Equal("agent-1", visible[0].AgentId);
    }

    [Fact]
    public async Task Subscription_limit_is_enforced()
    {
        await using var h = new StreamHarness(options: new TerminalConnectionOptions { MaxSubscriptions = 1 });
        await h.OpenTerminalAsync();
        await h.SendAsync(new TerminalOpen("req2", null, 80, 24));
        Assert.Equal(TerminalConnection.CodeSubscriptionLimit, (await h.NextAsync<TerminalError>()).Code);
        Assert.Single(h.Inner.Channel.Ptys);
    }

    [Fact]
    public async Task Broker_faults_become_internal_error_frames_not_exceptions()
    {
        var h = new Harness();
        var connection = new TerminalConnection(new ThrowingBroker(), Harness.Agent1, new FakeResolver());
        await connection.HandleInboundAsync(StreamHarness.Bytes(new TerminalInput("t1", [1])), StreamHarness.Ct);
        Assert.True(connection.Outbound.TryRead(out var raw));
        var error = Assert.IsType<TerminalError>(TerminalFrameCodec.Decode(raw.Span).Frame);
        Assert.Equal(TerminalConnection.CodeInternal, error.Code);
        Assert.Empty(connection.VisibleTerminals());
        await connection.DisposeAsync();
        await h.DisposeAsync();
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        await using var h = new StreamHarness();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await h.Connection.HandleInboundAsync(StreamHarness.Bytes(new TerminalInput("t1", [1])), cts.Token));
    }

    [Fact]
    public void Options_normalize_clamps_every_value()
    {
        var n = new TerminalConnectionOptions { OutboundQueueFrames = 0, MaxConsecutiveMalformed = 0, MaxSubscriptions = -1, MaxInboundFrameBytes = 1 }.Normalize();
        Assert.Equal(8, n.OutboundQueueFrames);
        Assert.Equal(1, n.MaxConsecutiveMalformed);
        Assert.Equal(1, n.MaxSubscriptions);
        Assert.Equal(1024, n.MaxInboundFrameBytes);
        Assert.Equal(256 * 1024, new TerminalConnectionOptions { MaxInboundFrameBytes = int.MaxValue }.Normalize().MaxInboundFrameBytes);
    }

    private sealed class ThrowingBroker : ITerminalBroker
    {
        public Task<(TerminalResult Result, TerminalInfo? Terminal)> OpenAsync(TerminalCaller caller, string agentId, string vmId, int cols, int rows, CancellationToken ct) => throw new InvalidOperationException("boom");

        public IReadOnlyList<TerminalInfo> List(TerminalCaller caller) => throw new InvalidOperationException("boom");

        public Task<(TerminalResult Result, ITerminalSubscription? Subscription)> SubscribeAsync(TerminalCaller caller, string terminalId, long fromSeq, CancellationToken ct) => throw new InvalidOperationException("boom");

        public Task<TerminalResult> WriteAsync(TerminalCaller caller, string terminalId, ReadOnlyMemory<byte> bytes, CancellationToken ct) => throw new InvalidOperationException("boom");

        public Task<TerminalResult> ResizeAsync(TerminalCaller caller, string terminalId, int cols, int rows, CancellationToken ct) => throw new InvalidOperationException("boom");

        public Task<TerminalResult> TakeControlAsync(TerminalCaller caller, string terminalId, CancellationToken ct) => throw new InvalidOperationException("boom");

        public Task<TerminalResult> ReleaseControlAsync(TerminalCaller caller, string terminalId, CancellationToken ct) => throw new InvalidOperationException("boom");

        public Task<TerminalResult> CloseAsync(TerminalCaller caller, string terminalId, CancellationToken ct) => throw new InvalidOperationException("boom");
    }
}
