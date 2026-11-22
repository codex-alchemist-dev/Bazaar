using Bazaar.Server.Terminal.Abstractions;

namespace Bazaar.Server.Terminal.Tests;

public class BrokerOpenAndLimitsTests
{
    [Fact]
    public async Task Open_fails_closed_with_NoBackend_when_the_channel_is_unavailable()
    {
        await using var h = new Harness();
        h.Channel.IsAvailable = false;

        var (result, info) = await h.Broker.OpenAsync(Harness.Agent1, "agent-1", "vm-1", 80, 24, Harness.Ct);

        Assert.Equal(TerminalRefusal.NoBackend, result.Refusal);
        Assert.Null(info);
        Assert.Empty(h.Channel.Ptys);
        Assert.Empty(h.Audit.Kinds);
    }

    [Theory]
    [InlineData(0, 24)]
    [InlineData(80, 0)]
    [InlineData(-1, 24)]
    [InlineData(1001, 24)]
    [InlineData(80, 1001)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public async Task Open_rejects_sizes_outside_1_to_1000(int cols, int rows)
    {
        await using var h = new Harness();
        var (result, _) = await h.Broker.OpenAsync(Harness.Agent1, "agent-1", "vm-1", cols, rows, Harness.Ct);
        Assert.Equal(TerminalRefusal.InvalidSize, result.Refusal);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1000, 1000)]
    public async Task Open_accepts_the_size_bounds(int cols, int rows)
    {
        await using var h = new Harness();
        var (result, info) = await h.Broker.OpenAsync(Harness.Agent1, "agent-1", "vm-1", cols, rows, Harness.Ct);
        Assert.True(result.Ok);
        Assert.Equal((cols, rows), (info!.Cols, info.Rows));
    }

    [Fact]
    public async Task Agent_open_makes_the_agent_the_writer_and_writes_an_open_audit_row()
    {
        await using var h = new Harness();
        var info = await h.OpenAsync();

        Assert.Equal("agent-1", info.WriterMemberId);
        Assert.Equal("agent-1", info.AgentId);
        Assert.Equal("vm-1", info.VmId);
        Assert.Equal(0, info.LastSeq);
        Assert.Equal(["open"], h.Audit.Kinds);
    }

    [Fact]
    public async Task Owner_open_for_an_agent_audits_open_and_control_take()
    {
        await using var h = new Harness();
        var info = await h.OpenAsync(Harness.Owner, "agent-1");
        Assert.Equal("owner", info.WriterMemberId);
        Assert.Equal(["open", "control.take"], h.Audit.Kinds);
    }

    [Fact]
    public async Task Agent_cannot_open_a_terminal_for_another_agent()
    {
        await using var h = new Harness();
        var (result, _) = await h.Broker.OpenAsync(Harness.Agent2, "agent-1", "vm-1", 80, 24, Harness.Ct);
        Assert.Equal(TerminalRefusal.Forbidden, result.Refusal);
        Assert.Empty(h.Channel.Ptys);
    }

    [Fact]
    public async Task Human_without_a_write_grant_cannot_open_and_with_one_can()
    {
        await using var h = new Harness();
        var (denied, _) = await h.Broker.OpenAsync(Harness.Human, "agent-1", "vm-1", 80, 24, Harness.Ct);
        Assert.Equal(TerminalRefusal.Forbidden, denied.Refusal);

        h.Grants.Write.Add("human");
        var (granted, info) = await h.Broker.OpenAsync(Harness.Human, "agent-1", "vm-1", 80, 24, Harness.Ct);
        Assert.True(granted.Ok);
        Assert.Equal("human", info!.WriterMemberId);
    }

    [Fact]
    public async Task Blank_ids_are_refused()
    {
        await using var h = new Harness();
        var (a, _) = await h.Broker.OpenAsync(Harness.Agent1, " ", "vm-1", 80, 24, Harness.Ct);
        var (b, _) = await h.Broker.OpenAsync(Harness.Agent1, "agent-1", "", 80, 24, Harness.Ct);
        var (c, _) = await h.Broker.OpenAsync(new TerminalCaller("", false, true), "agent-1", "vm-1", 80, 24, Harness.Ct);
        Assert.Equal(TerminalRefusal.NotFound, a.Refusal);
        Assert.Equal(TerminalRefusal.NotFound, b.Refusal);
        Assert.Equal(TerminalRefusal.NotFound, c.Refusal);
    }

    [Fact]
    public async Task Fifth_terminal_for_one_agent_hits_the_limit_and_closing_frees_a_slot()
    {
        await using var h = new Harness();
        var infos = new List<TerminalInfo>();
        for (var i = 0; i < 4; i++)
        {
            infos.Add(await h.OpenAsync());
        }

        var (result, _) = await h.Broker.OpenAsync(Harness.Agent1, "agent-1", "vm-1", 80, 24, Harness.Ct);
        Assert.Equal(TerminalRefusal.LimitReached, result.Refusal);

        // Another agent is not affected.
        var (other, _) = await h.Broker.OpenAsync(Harness.Agent2, "agent-2", "vm-2", 80, 24, Harness.Ct);
        Assert.True(other.Ok);

        Assert.True((await h.Broker.CloseAsync(Harness.Agent1, infos[0].TerminalId, Harness.Ct)).Ok);
        var (again, _) = await h.Broker.OpenAsync(Harness.Agent1, "agent-1", "vm-1", 80, 24, Harness.Ct);
        Assert.True(again.Ok);
    }

    [Fact]
    public async Task Company_limit_applies_across_agents()
    {
        await using var h = new Harness(new TerminalOptions { MaxSessionsPerCompany = 3, MaxSessionsPerAgent = 3 });
        for (var i = 0; i < 3; i++)
        {
            var caller = new TerminalCaller("a" + i, false, true);
            var (r, _) = await h.Broker.OpenAsync(caller, "a" + i, "vm", 80, 24, Harness.Ct);
            Assert.True(r.Ok);
        }

        var (result, _) = await h.Broker.OpenAsync(new TerminalCaller("a9", false, true), "a9", "vm", 80, 24, Harness.Ct);
        Assert.Equal(TerminalRefusal.LimitReached, result.Refusal);
    }

    [Fact]
    public async Task Concurrent_opens_never_exceed_the_agent_limit()
    {
        await using var h = new Harness();
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            Task.Run(() => h.Broker.OpenAsync(Harness.Agent1, "agent-1", "vm-1", 80, 24, Harness.Ct), Harness.Ct)));

        Assert.Equal(4, results.Count(r => r.Result.Ok));
        Assert.Equal(16, results.Count(r => r.Result.Refusal == TerminalRefusal.LimitReached));
        Assert.Equal(4, h.Channel.Ptys.Count);
    }

    [Fact]
    public async Task A_failed_guest_open_reports_NoBackend_and_frees_the_slot()
    {
        await using var h = new Harness(new TerminalOptions { MaxSessionsPerAgent = 1 });
        h.Channel.FailOpen = true;
        var (result, info) = await h.Broker.OpenAsync(Harness.Agent1, "agent-1", "vm-1", 80, 24, Harness.Ct);
        Assert.Equal(TerminalRefusal.NoBackend, result.Refusal);
        Assert.Null(info);

        h.Channel.FailOpen = false;
        var (ok, _) = await h.Broker.OpenAsync(Harness.Agent1, "agent-1", "vm-1", 80, 24, Harness.Ct);
        Assert.True(ok.Ok);
    }

    [Fact]
    public async Task Owner_open_is_refused_and_cleaned_up_when_the_audit_sink_is_down()
    {
        await using var h = new Harness();
        h.Audit.Fail = true;
        var (result, _) = await h.Broker.OpenAsync(Harness.Owner, "agent-1", "vm-1", 80, 24, Harness.Ct);
        Assert.Equal(TerminalRefusal.Forbidden, result.Refusal);
        await Harness.EventuallyAsync(() => h.Channel.Last.Disposed);
        Assert.Empty(h.Broker.List(Harness.Owner));
    }

    [Fact]
    public async Task Agent_open_still_works_when_the_audit_sink_is_down()
    {
        await using var h = new Harness();
        h.Audit.Fail = true;
        var (result, _) = await h.Broker.OpenAsync(Harness.Agent1, "agent-1", "vm-1", 80, 24, Harness.Ct);
        Assert.True(result.Ok);
    }

    [Fact]
    public async Task Cancelled_open_throws_and_frees_the_slot()
    {
        await using var h = new Harness(new TerminalOptions { MaxSessionsPerAgent = 1 });
        using var cts = new CancellationTokenSource();
        var channel = new CancellingChannel();
        await using var broker = new TerminalBroker(channel, new AllowAllPolicy(), h.Audit, new TerminalOptions { MaxSessionsPerAgent = 1 }, h.Time);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => broker.OpenAsync(Harness.Agent1, "agent-1", "vm", 80, 24, cts.Token));
        channel.Cancel = false;
        var (ok, _) = await broker.OpenAsync(Harness.Agent1, "agent-1", "vm", 80, 24, Harness.Ct);
        Assert.True(ok.Ok);
    }

    private sealed class CancellingChannel : IGuestPtyChannel
    {
        public bool Cancel { get; set; } = true;

        public bool IsAvailable => true;

        public Task<IGuestPty> OpenAsync(string vmId, string terminalId, int cols, int rows, CancellationToken ct) =>
            Cancel ? throw new OperationCanceledException() : Task.FromResult<IGuestPty>(new FakeGuestPty());
    }

    [Fact]
    public async Task Options_are_normalised_so_settings_cannot_go_below_the_floors()
    {
        await using var h = new Harness(new TerminalOptions
        {
            ScrollbackBytes = 10,
            SubscriberQueueFrames = 0,
            MaxSessionsPerAgent = 0,
            MaxSessionsPerCompany = -5,
            IdleClose = TimeSpan.Zero,
            MaxFramePayloadBytes = 10_000_000,
            InputBytesPerSecond = 0,
        });
        var o = h.Broker.Options;
        Assert.Equal(64 * 1024, o.ScrollbackBytes);
        Assert.Equal(8, o.SubscriberQueueFrames);
        Assert.Equal(1, o.MaxSessionsPerAgent);
        Assert.Equal(1, o.MaxSessionsPerCompany);
        Assert.Equal(TimeSpan.FromMinutes(1), o.IdleClose);
        Assert.Equal(64 * 1024, o.MaxFramePayloadBytes);
        Assert.Equal(1024, o.InputBytesPerSecond);
    }

    [Fact]
    public void Default_options_match_the_design_doc()
    {
        var o = new TerminalOptions().Normalize();
        Assert.Equal(1024 * 1024, o.ScrollbackBytes);
        Assert.Equal(256, o.SubscriberQueueFrames);
        Assert.Equal(4, o.MaxSessionsPerAgent);
        Assert.Equal(64, o.MaxSessionsPerCompany);
        Assert.Equal(TimeSpan.FromMinutes(60), o.IdleClose);
        Assert.Equal(16 * 1024, o.MaxFramePayloadBytes);
        Assert.Equal(64 * 1024, o.InputBytesPerSecond);
    }

    [Fact]
    public async Task Payload_limit_never_exceeds_the_ring_size()
    {
        await using var h = new Harness(new TerminalOptions { MaxFramePayloadBytes = 60 * 1024 });
        Assert.True(h.Broker.Options.MaxFramePayloadBytes <= h.Broker.Options.ScrollbackBytes);
    }
}
