using Bazaar.RyuseiFuda.Orrery;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Start;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Tests.Ryusei;

/// <summary>
/// The RyuseiFuda answer to <see cref="IWakeChainDepthSource"/> (doc 38, Chain depth), pure: a fake fire depth source, no
/// PostgreSQL. The SQL read itself is covered by <see cref="RyuseiWakeDepthSourceTests"/>.
/// </summary>
public sealed class RyuseiWakeChainDepthSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_non_ryusei_kind_is_not_answered_and_the_fire_source_is_not_asked()
    {
        var fires = new FakeFireDepths(new Dictionary<Guid, int>());
        var fireId = Guid.CreateVersion7();

        var depth = await new RyuseiWakeChainDepthSource(fires).ChainDepthAsync("mention", fireId.ToString("D"), Ct);

        Assert.Null(depth);
        Assert.Empty(fires.Asked);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    [InlineData(null)]
    public async Task A_source_ref_that_is_not_a_fire_id_is_not_answered(string? sourceRef)
    {
        var fires = new FakeFireDepths(new Dictionary<Guid, int>());

        var depth = await new RyuseiWakeChainDepthSource(fires).ChainDepthAsync("ryusei_timer", sourceRef, Ct);

        Assert.Null(depth);
        Assert.Empty(fires.Asked);
    }

    [Fact]
    public async Task A_guid_in_another_text_format_is_not_a_fire_id()
    {
        // The writers store the "D" form only, so a different spelling is not a fire id this adapter should read.
        var fireId = Guid.CreateVersion7();
        var fires = new FakeFireDepths(new Dictionary<Guid, int> { [fireId] = 1 });

        var depth = await new RyuseiWakeChainDepthSource(fires).ChainDepthAsync("ryusei_timer", fireId.ToString("N"), Ct);

        Assert.Null(depth);
        Assert.Empty(fires.Asked);
    }

    [Fact]
    public async Task A_fire_that_does_not_exist_reads_null()
    {
        var fires = new FakeFireDepths(new Dictionary<Guid, int>());
        var fireId = Guid.CreateVersion7();

        var depth = await new RyuseiWakeChainDepthSource(fires).ChainDepthAsync("ryusei_ping", fireId.ToString("D"), Ct);

        Assert.Null(depth);
        Assert.Equal([fireId], fires.Asked);
    }

    [Fact]
    public async Task A_found_fire_reads_the_depth_of_its_trigger()
    {
        var fireId = Guid.CreateVersion7();
        var fires = new FakeFireDepths(new Dictionary<Guid, int> { [fireId] = 2 });

        var depth = await new RyuseiWakeChainDepthSource(fires).ChainDepthAsync("ryusei_watcher", fireId.ToString("D"), Ct);

        Assert.Equal(2, depth);
        Assert.Equal([fireId], fires.Asked);
    }

    [Fact]
    public async Task The_ryusei_source_wins_over_the_default_in_either_registration_order()
    {
        foreach (var agentTurnsFirst in new[] { true, false })
        {
            var services = new ServiceCollection();
            services.AddSingleton<IRyuseiWakeDepthSource>(new FakeFireDepths(new Dictionary<Guid, int>()));
            var configuration = new ConfigurationBuilder().Build();
            var agentTurns = new AgentTurnsComponent();
            var ryusei = new RyuseiScheduleComponent();
            if (agentTurnsFirst)
            {
                agentTurns.AddServices(services, configuration);
                ryusei.AddServices(services, configuration);
            }
            else
            {
                ryusei.AddServices(services, configuration);
                agentTurns.AddServices(services, configuration);
            }

            using var provider = services.BuildServiceProvider();

            Assert.IsType<RyuseiWakeChainDepthSource>(provider.GetRequiredService<IWakeChainDepthSource>());
        }
    }

    [Fact]
    public async Task Agent_turns_alone_registers_a_default_that_never_reads_a_depth()
    {
        var services = new ServiceCollection();
        new AgentTurnsComponent().AddServices(services, new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        var source = provider.GetRequiredService<IWakeChainDepthSource>();

        Assert.IsType<NoWakeChainDepthSource>(source);
        Assert.Null(await source.ChainDepthAsync("ryusei_timer", Guid.CreateVersion7().ToString("D"), Ct));
    }

    /// <summary>Answers from a fixed table and records every fire id it was asked for.</summary>
    private sealed class FakeFireDepths(IReadOnlyDictionary<Guid, int> depths) : IRyuseiWakeDepthSource
    {
        public List<Guid> Asked { get; } = [];

        public Task<int?> ChainDepthForFireAsync(Guid fireId, CancellationToken ct)
        {
            Asked.Add(fireId);
            return Task.FromResult(depths.TryGetValue(fireId, out var depth) ? (int?)depth : null);
        }
    }
}
