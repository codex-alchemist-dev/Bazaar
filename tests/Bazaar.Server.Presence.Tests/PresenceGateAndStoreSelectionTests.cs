using Bazaar.Contracts.Gc;
using Bazaar.Contracts.Wire;
using Bazaar.Server.Presence.Abstractions;
using Bazaar.Server.Wire.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Presence.Tests;

/// <summary>The presence stream gate, and the durable store taking the place of the in-memory one.</summary>
public class PresenceGateAndStoreSelectionTests
{
    private sealed class FakeDurableStore : IDurablePresenceStore
    {
        public ValueTask<bool> UpsertAsync(PresenceRow row, CancellationToken ct) => ValueTask.FromResult(true);

        public ValueTask<PresenceRow?> GetAsync(Guid agentId, CancellationToken ct) => ValueTask.FromResult<PresenceRow?>(null);

        public ValueTask<IReadOnlyList<PresenceRow>> GetAllAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<PresenceRow>>([]);
    }

    [Theory]
    [InlineData(PrincipalKind.Owner, true)]
    [InlineData(PrincipalKind.Human, true)]
    [InlineData(PrincipalKind.Agent, false)]
    public async Task Humans_may_subscribe_and_agents_may_not(PrincipalKind kind, bool allowed)
    {
        var gate = new PresenceStreamGate();

        Assert.Equal(WireStreams.Presence, gate.Stream);
        Assert.Equal(allowed, await gate.CanSubscribeAsync(Guid.NewGuid(), kind, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Registration_exposes_the_gate_to_the_wire_layer()
    {
        var services = new ServiceCollection();
        new PresenceComponent().AddServices(services, new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        Assert.Contains(provider.GetServices<IStreamGate>(), g => g is PresenceStreamGate);
    }

    [Fact]
    public void A_registered_durable_store_replaces_the_in_memory_store()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDurablePresenceStore, FakeDurableStore>();
        new PresenceComponent().AddServices(services, new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        Assert.IsType<FakeDurableStore>(provider.GetRequiredService<IPresenceStore>());
    }
}
