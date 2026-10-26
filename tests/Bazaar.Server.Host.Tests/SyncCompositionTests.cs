using Bazaar.Contracts.Wire;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Wire.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Host.Tests;

public sealed class SyncCompositionTests
{
    private static ServiceCollection NewServices(Dictionary<string, string?>? config = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config ?? []).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBazaarSync(configuration);
        return services;
    }

    [Fact]
    public async Task Engine_and_dispatcher_resolve_once_the_missing_sources_are_supplied()
    {
        var services = NewServices();
        services.AddSingleton<IGcStreamSource, FakeStreamSource>();
        services.AddSingleton<IGcAccessPolicy, FakeAccessPolicy>();
        await using var sp = services.BuildServiceProvider(validateScopes: true);

        var dispatcher = sp.GetRequiredService<GcDispatcher>();
        var engine = sp.GetRequiredService<SubscriptionEngine>();

        Assert.NotNull(engine);
        Assert.Same(dispatcher, sp.GetRequiredService<GcDispatcher>());
        Assert.Equal(0, dispatcher.ActiveFeeds);
    }

    [Fact]
    public async Task Missing_sources_fail_at_resolution_not_at_build()
    {
        await using var sp = NewServices().BuildServiceProvider(validateScopes: true);

        Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<GcDispatcher>());
        Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<SubscriptionEngine>());
    }

    [Fact]
    public async Task Sync_options_bind_from_the_bazaar_sync_section()
    {
        var services = NewServices(new Dictionary<string, string?>
        {
            ["Bazaar:Sync:ReplayPageSize"] = "37",
            ["Bazaar:Sync:OutboundQueueMax"] = "9",
            ["Bazaar:Sync:PumpRetryDelay"] = "00:00:01",
        });
        await using var sp = services.BuildServiceProvider(validateScopes: true);

        var options = sp.GetRequiredService<IOptions<SyncOptions>>().Value;

        Assert.Equal(37, options.ReplayPageSize);
        Assert.Equal(9, options.OutboundQueueMax);
        Assert.Equal(TimeSpan.FromSeconds(1), options.PumpRetryDelay);
        Assert.Equal(2000, options.ReplayCutoffEntries);
    }

    [Fact]
    public async Task Sync_options_default_when_the_section_is_absent()
    {
        await using var sp = NewServices().BuildServiceProvider(validateScopes: true);

        var options = sp.GetRequiredService<IOptions<SyncOptions>>().Value;

        Assert.Equal(new SyncOptions().ReplayPageSize, options.ReplayPageSize);
        Assert.Equal(new SyncOptions().LiveBufferMax, options.LiveBufferMax);
    }

    [Fact]
    public async Task Existing_registrations_are_not_replaced()
    {
        var custom = new GcDispatcher(new FakeStreamSource(), new SyncOptions());
        var services = new ServiceCollection();
        services.AddSingleton(custom);
        services.AddBazaarSync(new ConfigurationBuilder().Build());
        await using var sp = services.BuildServiceProvider(validateScopes: true);

        Assert.Same(custom, sp.GetRequiredService<GcDispatcher>());
    }

    private sealed class FakeStreamSource : IGcStreamSource
    {
        public ValueTask<GcHead?> GetHeadAsync(Guid gcId, CancellationToken ct) => ValueTask.FromResult<GcHead?>(null);

        public ValueTask<IReadOnlyList<WireGcMessage>> ReadAsync(
            Guid gcId, long after, long editAfter, long afterEv, int limit, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<WireGcMessage>>([]);

        public ValueTask<IReadOnlyList<WireGcMessage>> ReadTailAsync(Guid gcId, int limit, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<WireGcMessage>>([]);

        public ValueTask<int> CountUnreadAsync(Guid gcId, Guid viewerId, CancellationToken ct) => ValueTask.FromResult(0);
    }

    private sealed class FakeAccessPolicy : IGcAccessPolicy
    {
        public ValueTask<bool> CanReadAsync(Guid principalId, Guid gcId, CancellationToken ct) => ValueTask.FromResult(false);
    }
}
