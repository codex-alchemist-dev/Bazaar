using Bazaar.Contracts.Terminal;
using Bazaar.Core;
using Bazaar.Server.Terminal.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Terminal.Tests;

public class MapperAndModuleTests
{
    [Fact]
    public void Every_refusal_has_a_distinct_stable_code_that_round_trips()
    {
        var refusals = Enum.GetValues<TerminalRefusal>().Where(r => r != TerminalRefusal.None).ToList();
        var codes = refusals.Select(TerminalFrameMapper.ErrorCode).ToList();

        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.All(codes, c => Assert.StartsWith("terminal.", c));
        foreach (var refusal in refusals)
        {
            Assert.Equal(refusal, TerminalFrameMapper.RefusalFromCode(TerminalFrameMapper.ErrorCode(refusal)));
        }

        Assert.Equal(string.Empty, TerminalFrameMapper.ErrorCode(TerminalRefusal.None));
        Assert.Null(TerminalFrameMapper.RefusalFromCode("terminal.nonsense"));
        Assert.Null(TerminalFrameMapper.RefusalFromCode(null));
    }

    [Theory]
    [InlineData(TerminalRefusal.NoBackend, "terminal.no_backend")]
    [InlineData(TerminalRefusal.NotFound, "terminal.not_found")]
    [InlineData(TerminalRefusal.Forbidden, "terminal.forbidden")]
    [InlineData(TerminalRefusal.WriterHeld, "terminal.writer_held")]
    [InlineData(TerminalRefusal.LimitReached, "terminal.limit_reached")]
    [InlineData(TerminalRefusal.InvalidSize, "terminal.invalid_size")]
    [InlineData(TerminalRefusal.RateLimited, "terminal.rate_limited")]
    public void Codes_are_frozen_wire_strings(TerminalRefusal refusal, string code) =>
        Assert.Equal(code, TerminalFrameMapper.ErrorCode(refusal));

    [Fact]
    public void Output_chunk_round_trips_through_a_frame_without_sharing_the_ring_buffer()
    {
        var ring = new byte[] { 1, 2, 3 };
        var chunk = new TerminalOutputChunk(42, ring, IsReplay: true);

        var frame = TerminalFrameMapper.ToOutput("t1", chunk);
        ring[0] = 99;

        Assert.Equal("t1", frame.TerminalId);
        Assert.Equal(42, frame.Seq);
        Assert.Equal(new byte[] { 1, 2, 3 }, frame.Bytes);
        var back = TerminalFrameMapper.FromOutput(frame);
        Assert.Equal(42, back.Seq);
        Assert.Equal(new byte[] { 1, 2, 3 }, back.Bytes.ToArray());
        Assert.False(back.IsReplay);
    }

    [Fact]
    public void Opened_exit_error_and_control_frames_carry_the_right_fields()
    {
        var info = new TerminalInfo("t1", "a", "vm", 80, 24, "writer", 9);
        Assert.Equal(new TerminalOpened("t1", true, 5), TerminalFrameMapper.ToOpened(info, 5));
        Assert.False(TerminalFrameMapper.ToOpened(info with { WriterMemberId = null }, 1).WriterHeld);
        Assert.Equal(new TerminalExit("t1", 7), TerminalFrameMapper.ToExit("t1", 7));
        Assert.Equal("granted", TerminalFrameMapper.ToControlReply("t1", true).Action);
        Assert.Equal("denied", TerminalFrameMapper.ToControlReply("t1", false).Action);

        var error = TerminalFrameMapper.ToError("t1", new TerminalResult(TerminalRefusal.RateLimited, "slow down"));
        Assert.Equal(new TerminalError("t1", "terminal.rate_limited", "slow down"), error);
        Assert.Equal("Forbidden", TerminalFrameMapper.ToError("t1", new TerminalResult(TerminalRefusal.Forbidden)).Message);
    }

    [Fact]
    public async Task Handle_maps_input_resize_control_and_close_frames_onto_the_broker()
    {
        await using var h = new Harness();
        var info = await h.OpenAsync();
        var id = info.TerminalId;

        Assert.Null(await TerminalFrameMapper.HandleAsync(h.Broker, Harness.Agent1, new TerminalInput(id, [65]), Harness.Ct));
        Assert.Equal([65], h.Channel.Last.AllWritten.Select(b => (int)b));
        Assert.Null(await TerminalFrameMapper.HandleAsync(h.Broker, Harness.Agent1, new TerminalResize(id, 100, 50), Harness.Ct));
        Assert.Equal([(100, 50)], h.Channel.Last.Resizes);

        var denied = await TerminalFrameMapper.HandleAsync(h.Broker, Harness.Human, new TerminalInput(id, [1]), Harness.Ct);
        Assert.Equal("terminal.forbidden", Assert.IsType<TerminalError>(denied).Code);
        var badSize = await TerminalFrameMapper.HandleAsync(h.Broker, Harness.Agent1, new TerminalResize(id, 0, 0), Harness.Ct);
        Assert.Equal("terminal.invalid_size", Assert.IsType<TerminalError>(badSize).Code);

        var release = await TerminalFrameMapper.HandleAsync(h.Broker, Harness.Agent1, new TerminalControl(id, "release"), Harness.Ct);
        Assert.Equal("granted", Assert.IsType<TerminalControl>(release).Action);
        var take = await TerminalFrameMapper.HandleAsync(h.Broker, Harness.Agent1, new TerminalControl(id, "take"), Harness.Ct);
        Assert.Equal("granted", Assert.IsType<TerminalControl>(take).Action);
        var held = await TerminalFrameMapper.HandleAsync(h.Broker, Harness.Owner, new TerminalControl(id, "take"), Harness.Ct);
        Assert.Equal("denied", Assert.IsType<TerminalControl>(held).Action);
        var unknown = await TerminalFrameMapper.HandleAsync(h.Broker, Harness.Agent1, new TerminalControl(id, "steal"), Harness.Ct);
        Assert.Equal("terminal.bad_request", Assert.IsType<TerminalError>(unknown).Code);

        Assert.Null(await TerminalFrameMapper.HandleAsync(h.Broker, Harness.Agent1, new TerminalClose(id), Harness.Ct));
        var gone = await TerminalFrameMapper.HandleAsync(h.Broker, Harness.Agent1, new TerminalClose(id), Harness.Ct);
        Assert.Equal("terminal.not_found", Assert.IsType<TerminalError>(gone).Code);

        Assert.Null(await TerminalFrameMapper.HandleAsync(h.Broker, Harness.Agent1, new TerminalReplay(id, 1), Harness.Ct));
    }

    [Fact]
    public void Manifest_is_valid_and_matches_the_design()
    {
        var m = new TerminalModule().Manifest;
        Assert.Equal("terminal", m.Id);
        Assert.Empty(m.DependsOn);
        Assert.Equal(["vms", "audit"], m.Soft);
        Assert.Matches("^[a-z][a-z0-9]*(-[a-z0-9]+)*$", m.Id);
        Assert.Equal("terminal", m.MigrationNamespace);
    }

    [Fact]
    public void Manifest_orders_with_or_without_its_soft_dependencies()
    {
        var terminal = TerminalModule.ManifestDefinition;
        Assert.Equal(["terminal"], ComponentGraph.Order([terminal]).Select(x => x.Id));

        var vms = new ComponentManifest("vms", "0.1.0", ComponentKind.Feature, [], []);
        var audit = new ComponentManifest("audit", "0.1.0", ComponentKind.Feature, [], []);
        var ordered = ComponentGraph.Order([terminal, vms, audit]).Select(x => x.Id).ToList();
        Assert.Equal(3, ordered.Count);
        Assert.Contains("terminal", ordered);
    }

    [Fact]
    public async Task Module_registration_fails_closed_until_a_backend_and_audit_are_registered()
    {
        var services = new ServiceCollection();
        new TerminalModule().AddServices(services, new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider();
        var broker = provider.GetRequiredService<ITerminalBroker>();
        Assert.IsType<TerminalBroker>(broker);

        var (result, _) = await broker.OpenAsync(Harness.Agent1, "agent-1", "vm", 80, 24, Harness.Ct);
        Assert.Equal(TerminalRefusal.NoBackend, result.Refusal);
        Assert.IsType<DefaultTerminalAccessPolicy>(provider.GetRequiredService<ITerminalAccessPolicy>());
    }

    [Fact]
    public async Task Earlier_registrations_win_so_the_vm_module_and_audit_module_plug_in()
    {
        var channel = new FakeChannel();
        var audit = new RecordingAudit();
        var services = new ServiceCollection();
        services.AddSingleton<IGuestPtyChannel>(channel);
        services.AddSingleton<ITerminalAudit>(audit);
        services.AddBazaarTerminal();
        await using var provider = services.BuildServiceProvider();
        var broker = provider.GetRequiredService<ITerminalBroker>();

        var (result, _) = await broker.OpenAsync(Harness.Agent1, "agent-1", "vm", 80, 24, Harness.Ct);
        Assert.True(result.Ok);
        Assert.Equal(["open"], audit.Kinds);
    }

    [Fact]
    public async Task Default_audit_sink_blocks_human_control_but_not_agent_work()
    {
        var channel = new FakeChannel();
        var services = new ServiceCollection();
        services.AddSingleton<IGuestPtyChannel>(channel);
        services.AddBazaarTerminal();
        await using var provider = services.BuildServiceProvider();
        var broker = provider.GetRequiredService<ITerminalBroker>();

        Assert.True((await broker.OpenAsync(Harness.Agent1, "agent-1", "vm", 80, 24, Harness.Ct)).Result.Ok);
        var (ownerOpen, _) = await broker.OpenAsync(Harness.Owner, "agent-1", "vm", 80, 24, Harness.Ct);
        Assert.Equal(TerminalRefusal.Forbidden, ownerOpen.Refusal);
    }

    [Fact]
    public void Operator_configuration_is_read_and_clamped()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Terminal:ScrollbackBytes"] = "10",
            ["Terminal:MaxSessionsPerAgent"] = "2",
            ["Terminal:SubscriberQueueFrames"] = "not a number",
            ["Terminal:AuditEveryHumanInput"] = "true",
        }).Build();
        var services = new ServiceCollection();
        new TerminalModule().AddServices(services, config);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<TerminalOptions>();

        Assert.Equal(64 * 1024, options.ScrollbackBytes);
        Assert.Equal(2, options.MaxSessionsPerAgent);
        Assert.Equal(256, options.SubscriberQueueFrames);
        Assert.True(options.AuditEveryHumanInput);
    }
}
