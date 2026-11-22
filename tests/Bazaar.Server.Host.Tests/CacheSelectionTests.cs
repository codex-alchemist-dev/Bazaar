using Bazaar.Server.Host.Composition;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Host.Tests;

/// <summary>The deferred cache selection: its ordering in the host graph, its skip and failure paths, and the wrapper consumers see while it is pending.</summary>
public sealed class CacheSelectionTests
{
    private sealed class FakeCache(string kind, List<string>? events = null) : ICacheProvider
    {
        public string Kind => kind;

        public int Sets { get; private set; }

        public int Gets { get; private set; }

        public int Deletes { get; private set; }

        public ProviderStatus Probe()
        {
            events?.Add("probe");
            return new ProviderStatus(kind, true, [], null);
        }

        public Task SetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan ttl, CancellationToken ct)
        {
            Sets++;
            return Task.CompletedTask;
        }

        public Task<ReadOnlyMemory<byte>?> GetAsync(string key, CancellationToken ct)
        {
            Gets++;
            return Task.FromResult<ReadOnlyMemory<byte>?>(new byte[] { 1 });
        }

        public Task DeleteAsync(string key, CancellationToken ct)
        {
            Deletes++;
            return Task.CompletedTask;
        }
    }

    /// <summary>A hosted service that records when it starts, and runs an optional action (the instance lock uses it to mark the host started).</summary>
    private sealed class RecordingStep(string name, List<string> events, Action? onStart = null) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            events.Add(name);
            onStart?.Invoke();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>The graph in the real order: the instance lock, then the supervisor, then the cache selection registered by the host.</summary>
    private static ServiceProvider Graph(string cache, List<string> events, bool lockAcquired)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:Providers:ServiceHost"] = "none",
            ["Bazaar:Providers:Cache"] = cache,
        }).Build();
        var state = new HostState(TimeProvider.System);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(state);
        services.AddSingleton<IHostedService>(new RecordingStep("lock", events, () =>
        {
            if (lockAcquired)
            {
                state.MarkStarted();
            }
        }));
        services.AddSingleton<IHostedService>(new RecordingStep("supervisor", events));
        services.AddBazaarProviders(configuration);
        services.AddBazaarCacheSelection();
        services.AddSingleton<ICacheProvider>(new FakeCache("fake", events));
        return services.BuildServiceProvider();
    }

    private static async Task StartAllAsync(ServiceProvider provider)
    {
        var ct = TestContext.Current.CancellationToken;
        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            await hosted.StartAsync(ct);
        }
    }

    [Fact]
    public async Task The_cache_is_selected_after_the_instance_lock_and_after_the_supervisor_started()
    {
        var events = new List<string>();
        await using var provider = Graph("fake", events, lockAcquired: true);

        await StartAllAsync(provider);

        Assert.Equal(["lock", "supervisor", "probe"], events);
        var report = provider.GetRequiredService<ProviderCapabilityReport>();
        var cache = report.Selections.Single(s => s.Role == ProviderRole.Cache);
        Assert.False(cache.IsPending);
        Assert.Equal("fake", cache.Provider!.Kind);
        Assert.Equal("fake", provider.GetRequiredService<DeferredCacheProvider>().Probe().Kind);
    }

    [Fact]
    public async Task While_the_selection_is_pending_the_report_says_so_and_the_wrapper_degrades()
    {
        var events = new List<string>();
        await using var provider = Graph("fake", events, lockAcquired: true);

        var report = provider.GetRequiredService<ProviderCapabilityReport>();
        var pending = report.Selections.Single(s => s.Role == ProviderRole.Cache);
        var wrapper = provider.GetServices<ICacheProvider>().OfType<DeferredCacheProvider>().Single();

        Assert.True(pending.IsPending);
        Assert.Equal("provider cache: pending (requested fake)", pending.ToLogLine());
        Assert.False(wrapper.Probe().Usable);
        Assert.Equal("cache selection is pending", wrapper.Probe().Reason);
        Assert.Null(await wrapper.GetAsync("k", TestContext.Current.CancellationToken));
        Assert.Empty(events);
    }

    [Fact]
    public async Task A_lock_that_was_not_acquired_leaves_the_cache_unselected_and_never_probes_it()
    {
        var events = new List<string>();
        await using var provider = Graph("fake", events, lockAcquired: false);

        await StartAllAsync(provider);

        Assert.Equal(["lock", "supervisor"], events);
        var cache = provider.GetRequiredService<ProviderCapabilityReport>().Selections.Single(s => s.Role == ProviderRole.Cache);
        Assert.False(cache.IsPending);
        Assert.Null(cache.Provider);
        Assert.True(provider.GetRequiredService<DeferredCacheProvider>().IsResolved);
    }

    [Fact]
    public async Task A_cache_that_cannot_be_selected_is_left_out_and_the_start_goes_on()
    {
        var events = new List<string>();
        await using var provider = Graph("garnet", events, lockAcquired: true);

        await StartAllAsync(provider);

        var cache = provider.GetRequiredService<ProviderCapabilityReport>().Selections.Single(s => s.Role == ProviderRole.Cache);
        Assert.Null(cache.Provider);
        Assert.False(cache.IsPending);
        var status = Assert.Single(cache.Statuses);
        Assert.Equal("selection failed (ProviderSelectionException)", status.Reason);
    }

    [Fact]
    public async Task A_cache_set_to_none_is_resolved_at_once_and_nothing_is_probed()
    {
        var events = new List<string>();
        await using var provider = Graph("none", events, lockAcquired: true);

        await StartAllAsync(provider);

        Assert.Equal(["lock", "supervisor"], events);
        var cache = provider.GetRequiredService<ProviderCapabilityReport>().Selections.Single(s => s.Role == ProviderRole.Cache);
        Assert.False(cache.IsPending);
        Assert.Equal("provider cache: none (disabled by setting)", cache.ToLogLine());
    }

    [Fact]
    public async Task The_wrapper_delegates_to_the_selected_cache_once_resolved()
    {
        var cache = new FakeCache("fake");
        var wrapper = new DeferredCacheProvider();
        wrapper.Resolve(cache);
        var ct = TestContext.Current.CancellationToken;

        await wrapper.SetAsync("k", new byte[] { 1 }, TimeSpan.FromMinutes(1), ct);
        var value = await wrapper.GetAsync("k", ct);
        await wrapper.DeleteAsync("k", ct);

        Assert.Equal(1, cache.Sets);
        Assert.Equal(1, cache.Gets);
        Assert.Equal(1, cache.Deletes);
        Assert.NotNull(value);
        Assert.True(wrapper.Probe().Usable);
    }

    [Fact]
    public async Task The_wrapper_degrades_when_resolved_to_no_cache()
    {
        var wrapper = new DeferredCacheProvider();
        wrapper.Resolve(null);
        var ct = TestContext.Current.CancellationToken;

        await wrapper.SetAsync("k", new byte[] { 1 }, TimeSpan.FromMinutes(1), ct);

        Assert.Null(await wrapper.GetAsync("k", ct));
        Assert.Equal("no cache is selected", wrapper.Probe().Reason);
    }
}
