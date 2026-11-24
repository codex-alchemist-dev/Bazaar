using Bazaar.Server.Terminal.Abstractions;

namespace Bazaar.Server.Terminal.Tests;

public class BrokerLifecycleTests
{
    [Fact]
    public async Task Idle_session_is_closed_after_60_minutes_with_no_input_and_no_output()
    {
        await using var h = new Harness();
        var info = await h.OpenAsync();
        await using var sub = await h.SubscribeAsync(Harness.Owner, info.TerminalId);

        h.Time.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(0, await h.Broker.SweepAsync());
        Assert.Single(h.Broker.List(Harness.Owner));

        h.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await h.Broker.SweepAsync());
        Assert.Empty(h.Broker.List(Harness.Owner));
        Assert.True(h.Channel.Last.Disposed);
        Assert.Equal(-1, await sub.Exited.WaitAsync(TimeSpan.FromSeconds(5), Harness.Ct));
        await sub.Output.Completion.WaitAsync(TimeSpan.FromSeconds(5), Harness.Ct);
        Assert.Contains("close", h.Audit.Kinds);
        Assert.Equal(TerminalRefusal.NotFound, (await h.Broker.WriteAsync(Harness.Agent1, info.TerminalId, new byte[1], Harness.Ct)).Refusal);
    }

    [Fact]
    public async Task Output_and_input_each_reset_the_idle_clock()
    {
        await using var h = new Harness();
        var info = await h.OpenAsync();

        h.Time.Advance(TimeSpan.FromMinutes(50));
        h.Channel.Last.Emit("tick");
        await Harness.EventuallyAsync(() => h.Broker.List(Harness.Owner)[0].LastSeq == 1);
        h.Time.Advance(TimeSpan.FromMinutes(50));
        Assert.Equal(0, await h.Broker.SweepAsync());

        await h.Broker.WriteAsync(Harness.Agent1, info.TerminalId, new byte[] { 1 }, Harness.Ct);
        h.Time.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(0, await h.Broker.SweepAsync());
        h.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await h.Broker.SweepAsync());
    }

    [Fact]
    public async Task Idle_close_frees_the_agent_slot()
    {
        await using var h = new Harness(new TerminalOptions { MaxSessionsPerAgent = 1 });
        await h.OpenAsync();
        h.Time.Advance(TimeSpan.FromMinutes(61));
        await h.Broker.SweepAsync();
        var (result, _) = await h.Broker.OpenAsync(Harness.Agent1, "agent-1", "vm-1", 80, 24, Harness.Ct);
        Assert.True(result.Ok);
    }

    [Fact]
    public async Task Guest_exit_completes_subscribers_with_the_code_after_all_output()
    {
        await using var h = new Harness();
        var info = await h.OpenAsync();
        await using var sub = await h.SubscribeAsync(Harness.Owner, info.TerminalId);
        h.Channel.Last.Emit("bye");
        h.Channel.Last.Exit(3);

        Assert.Equal(3, await sub.Exited.WaitAsync(TimeSpan.FromSeconds(5), Harness.Ct));
        var chunk = await Harness.ReadAsync(sub);
        Assert.Equal("bye", System.Text.Encoding.UTF8.GetString(chunk.Bytes.Span));
        await sub.Output.Completion.WaitAsync(TimeSpan.FromSeconds(5), Harness.Ct);
        Assert.False(await sub.Output.WaitToReadAsync(Harness.Ct));
    }

    [Fact]
    public async Task Exit_with_a_lagging_viewer_still_delivers_everything_before_completing()
    {
        await using var h = new Harness(new TerminalOptions { SubscriberQueueFrames = 8 });
        var info = await h.OpenAsync();
        await using var sub = await h.SubscribeAsync(Harness.Owner, info.TerminalId);
        for (var i = 0; i < 30; i++)
        {
            h.Channel.Last.Emit("e" + i);
        }

        h.Channel.Last.Exit(0);
        await sub.Exited.WaitAsync(TimeSpan.FromSeconds(5), Harness.Ct);

        var seqs = new List<long>();
        await foreach (var chunk in sub.Output.ReadAllAsync(Harness.Ct))
        {
            seqs.Add(chunk.Seq);
        }

        Assert.Equal(Enumerable.Range(1, 30).Select(i => (long)i), seqs);
    }

    [Fact]
    public async Task An_exited_session_lingers_for_late_viewers_then_disappears()
    {
        await using var h = new Harness();
        var info = await h.OpenAsync();
        h.Channel.Last.Emit("last words");
        h.Channel.Last.Exit(1);
        await Harness.EventuallyAsync(() => h.Channel.Last.Disposed);

        // Late viewer during the linger sees the output and the exit.
        await using var late = await h.SubscribeAsync(Harness.Owner, info.TerminalId);
        Assert.Equal(1, await late.Exited.WaitAsync(TimeSpan.FromSeconds(5), Harness.Ct));
        Assert.Equal("last words", System.Text.Encoding.UTF8.GetString((await Harness.ReadAsync(late)).Bytes.Span));
        await late.Output.Completion.WaitAsync(TimeSpan.FromSeconds(5), Harness.Ct);

        // Input is refused once the shell ended.
        Assert.Equal(TerminalRefusal.NotFound, (await h.Broker.WriteAsync(Harness.Agent1, info.TerminalId, new byte[1], Harness.Ct)).Refusal);

        h.Time.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(TerminalRefusal.NotFound, (await h.Broker.SubscribeAsync(Harness.Owner, info.TerminalId, 0, Harness.Ct)).Result.Refusal);
        Assert.Empty(h.Broker.List(Harness.Owner));
    }

    [Fact]
    public async Task An_exited_session_no_longer_counts_toward_limits()
    {
        await using var h = new Harness(new TerminalOptions { MaxSessionsPerAgent = 1 });
        await h.OpenAsync();
        h.Channel.Last.Exit(0);
        await Harness.EventuallyAsync(() => h.Channel.Last.Disposed);
        var (result, _) = await h.Broker.OpenAsync(Harness.Agent1, "agent-1", "vm-1", 80, 24, Harness.Ct);
        Assert.True(result.Ok);
    }

    [Fact]
    public async Task Sweep_forgets_ended_sessions_after_the_linger()
    {
        await using var h = new Harness();
        await h.OpenAsync();
        h.Channel.Last.Exit(0);
        await Harness.EventuallyAsync(() => h.Channel.Last.Disposed);
        h.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, await h.Broker.SweepAsync());
        Assert.Empty(h.Broker.List(Harness.Owner));
    }

    [Fact]
    public async Task Take_after_exit_is_NotFound_and_exit_clears_the_writer()
    {
        await using var h = new Harness();
        var info = await h.OpenAsync();
        h.Channel.Last.Exit(0);
        await Harness.EventuallyAsync(() => h.Channel.Last.Disposed);
        Assert.Equal(TerminalRefusal.NotFound, (await h.Broker.TakeControlAsync(Harness.Agent1, info.TerminalId, Harness.Ct)).Refusal);
    }

    [Fact]
    public async Task Close_by_the_owning_agent_completes_subscribers_audits_and_removes_the_session()
    {
        await using var h = new Harness();
        var info = await h.OpenAsync();
        await using var sub = await h.SubscribeAsync(Harness.Owner, info.TerminalId);

        Assert.True((await h.Broker.CloseAsync(Harness.Agent1, info.TerminalId, Harness.Ct)).Ok);
        Assert.Equal(-1, await sub.Exited.WaitAsync(TimeSpan.FromSeconds(5), Harness.Ct));
        await sub.Output.Completion.WaitAsync(TimeSpan.FromSeconds(5), Harness.Ct);
        Assert.True(h.Channel.Last.Disposed);
        Assert.Equal(["open", "close"], h.Audit.Kinds);
        Assert.Empty(h.Broker.List(Harness.Owner));
        Assert.Equal(TerminalRefusal.NotFound, (await h.Broker.CloseAsync(Harness.Agent1, info.TerminalId, Harness.Ct)).Refusal);
    }

    [Fact]
    public async Task Close_is_forbidden_to_other_agents_and_ungranted_humans()
    {
        await using var h = new Harness();
        var info = await h.OpenAsync();
        Assert.Equal(TerminalRefusal.Forbidden, (await h.Broker.CloseAsync(Harness.Agent2, info.TerminalId, Harness.Ct)).Refusal);
        Assert.Equal(TerminalRefusal.Forbidden, (await h.Broker.CloseAsync(Harness.Human, info.TerminalId, Harness.Ct)).Refusal);
        Assert.Single(h.Broker.List(Harness.Owner));
    }

    [Fact]
    public async Task Closing_the_broker_ends_every_session()
    {
        var h = new Harness();
        await h.OpenAsync();
        await h.OpenAsync();
        await h.DisposeAsync();
        Assert.All(h.Channel.Ptys, p => Assert.True(p.Disposed));
        Assert.Empty(h.Broker.List(Harness.Owner));
    }

    [Fact]
    public async Task Audit_rows_cover_open_take_release_input_and_close_in_order()
    {
        await using var h = new Harness();
        h.Grants.Write.Add("human");
        var info = await h.OpenAsync();
        await h.Broker.ReleaseControlAsync(Harness.Agent1, info.TerminalId, Harness.Ct);
        await h.Broker.TakeControlAsync(Harness.Human, info.TerminalId, Harness.Ct);
        await h.Broker.WriteAsync(Harness.Human, info.TerminalId, new byte[] { 1 }, Harness.Ct);
        await h.Broker.ReleaseControlAsync(Harness.Human, info.TerminalId, Harness.Ct);
        await h.Broker.CloseAsync(Harness.Owner, info.TerminalId, Harness.Ct);

        Assert.Equal(
            ["open", "control.release", "control.take", "input.human", "control.release", "close"],
            h.Audit.Kinds);
        Assert.All(h.Audit.Rows, r => Assert.Equal(info.TerminalId, r.TerminalId));
    }
}
