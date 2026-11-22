using Bazaar.Contracts.Engines;
using Bazaar.Contracts.Gc;
using Bazaar.Server.EngineDispatch;
using Bazaar.Server.Wire.Connection;
using Bazaar.Server.Wire.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.EngineRelay.Tests;

public sealed class EngineRelayComponentTests
{
    [Theory]
    [InlineData(PrincipalKind.Owner, true)]
    [InlineData(PrincipalKind.Human, false)]
    [InlineData(PrincipalKind.Agent, false)]
    public async Task Only_the_owner_may_subscribe(PrincipalKind kind, bool expected)
    {
        var gate = new EngineRelayStreamGate();

        Assert.Equal(EngineRelayWire.Stream, gate.Stream);
        Assert.Equal(expected, await gate.CanSubscribeAsync(Guid.NewGuid(), kind, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Component_registers_the_gate_handlers_and_a_resolvable_broker()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWireConnectionDirectory>(new EmptyDirectory());
        new EngineRelayComponent().AddServices(services, new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Engine:Relay:TimeoutSeconds"] = "5" }).Build());

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        Assert.Contains(provider.GetServices<IStreamGate>(), g => g.Stream == EngineRelayWire.Stream);
        Assert.Equal(2, provider.GetServices<IWireRequestHandler>().Count());
        Assert.Equal(TimeSpan.FromSeconds(5), provider.GetRequiredService<EngineRelayOptions>().Timeout);
        Assert.NotNull(provider.GetRequiredService<EngineRelayBroker>());
    }

    [Fact]
    public async Task Unsupplied_seams_fail_closed()
    {
        var reader = new DenyingEngineGrantReader();
        Assert.False(await reader.CoversAsync(Guid.NewGuid(), "roblox", "script_read", RiskClass.Read, "g", TestContext.Current.CancellationToken));
        Assert.True(await new TaintedEngineSessionReader().IsHardTaintedAsync(Guid.NewGuid(), null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Supplied_seams_win_over_the_fail_closed_defaults_whatever_the_order()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWireConnectionDirectory>(new EmptyDirectory());
        new EngineRelayComponent().AddServices(services, new ConfigurationBuilder().Build());
        // Registered after the relay component: still used.
        services.AddSingleton<IEngineGrantReader>(new AllowAll());
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<EngineToolDispatcher>());
        Assert.IsType<AllowAll>(provider.GetRequiredService<IEngineGrantReader>());
        await Task.CompletedTask;
    }

    private sealed class AllowAll : IEngineGrantReader
    {
        public ValueTask<bool> CoversAsync(Guid agentId, string adapterId, string toolName, RiskClass riskClass, string grantId, CancellationToken ct) =>
            ValueTask.FromResult(true);
    }

    private sealed class EmptyDirectory : IWireConnectionDirectory
    {
        public IReadOnlyCollection<IWireConnection> Connections { get; } = [];
    }
}
