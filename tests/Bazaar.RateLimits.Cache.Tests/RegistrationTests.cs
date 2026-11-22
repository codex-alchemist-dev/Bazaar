using Bazaar.Core;
using Bazaar.Server.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Bazaar.RateLimits.Cache.Tests;

public sealed class RegistrationTests
{
    private static readonly RateLimitPolicy _policy = new("reg_policy", 1, TimeSpan.FromMinutes(1), RateLimitScope.Installation, "reg.key");

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static ServiceProvider Build(Action<IServiceCollection> before, Action<IServiceCollection>? after = null, IConfiguration? config = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        before(services);
        services.AddBazaarCacheRateLimits(config ?? Config());
        after?.Invoke(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void The_cache_limiter_wins_over_earlier_registrations_of_every_kind()
    {
        using var sp = Build(s =>
        {
            s.AddBazaarRateLimits();
            s.TryAddSingleton<IRateLimiter>(new InMemoryRateLimiter(TimeProvider.System));
            s.AddSingleton<IRateLimiter>(new InMemoryRateLimiter(TimeProvider.System));
        });

        Assert.IsType<CacheRateLimiter>(sp.GetRequiredService<IRateLimiter>());
        Assert.Single(sp.GetServices<IRateLimiter>());
    }

    [Fact]
    public void The_cache_limiter_wins_over_later_try_add_registrations()
    {
        using var sp = Build(_ => { }, s =>
        {
            s.AddBazaarRateLimits();
            s.TryAddSingleton<IRateLimiter>(new InMemoryRateLimiter(TimeProvider.System));
        });

        Assert.IsType<CacheRateLimiter>(sp.GetRequiredService<IRateLimiter>());
    }

    [Fact]
    public void With_no_cache_provider_the_resolved_limiter_still_limits_in_process()
    {
        using var sp = Build(_ => { });
        var limiter = sp.GetRequiredService<IRateLimiter>();

        Assert.True(limiter.TryAcquire(_policy, "a").Allowed);
        Assert.False(limiter.TryAcquire(_policy, "a").Allowed);
    }

    [Fact]
    public void A_provider_that_cannot_count_is_skipped_and_the_limiter_still_works()
    {
        using var sp = Build(s => s.AddSingleton<ICacheProvider, PlainProvider>());
        var limiter = sp.GetRequiredService<IRateLimiter>();

        Assert.True(limiter.TryAcquire(_policy, "a").Allowed);
        Assert.False(limiter.TryAcquire(_policy, "a").Allowed);
    }

    [Fact]
    public void The_first_provider_that_can_count_is_the_shared_counter()
    {
        var counter = new CountingProvider();
        using var sp = Build(s =>
        {
            s.AddSingleton<ICacheProvider, PlainProvider>();
            s.AddSingleton<ICacheProvider>(counter);
        });
        var limiter = sp.GetRequiredService<IRateLimiter>();

        Assert.True(limiter.TryAcquire(_policy, "a").Allowed);
        Assert.False(limiter.TryAcquire(_policy, "a").Allowed);

        Assert.NotEmpty(counter.Keys);
    }

    [Fact]
    public void The_cache_timeout_binds_from_configuration()
    {
        using var sp = Build(_ => { }, config: Config(("Bazaar:RateLimits:CacheTimeout", "00:00:00.300")));

        Assert.Equal(TimeSpan.FromMilliseconds(300), sp.GetRequiredService<IOptions<CacheRateLimiterOptions>>().Value.CacheTimeout);
    }

    [Fact]
    public void An_invalid_cache_timeout_is_rejected_when_the_options_are_read()
    {
        using var sp = Build(_ => { }, config: Config(("Bazaar:RateLimits:CacheTimeout", "00:00:00")));

        Assert.Throws<OptionsValidationException>(() => sp.GetRequiredService<IOptions<CacheRateLimiterOptions>>().Value);
    }

    [Fact]
    public void The_component_registers_the_limiter_and_declares_the_cache_modules_as_soft_dependencies()
    {
        var component = new RateLimitsCacheComponent();
        var services = new ServiceCollection();

        component.AddServices(services, Config());

        Assert.Equal("rate-limits-cache", component.Manifest.Id);
        Assert.Empty(component.Manifest.DependsOn);
        Assert.Equal(["valkey", "garnet"], component.Manifest.Soft);
        Assert.Contains(services, d => d.ServiceType == typeof(IRateLimiter));
    }

    private class PlainProvider : ICacheProvider
    {
        public string Kind => "plain";

        public ProviderStatus Probe() => new(Kind, true, [], null);

        public Task SetAsync(string key, ReadOnlyMemory<byte> value, TimeSpan ttl, CancellationToken ct) => Task.CompletedTask;

        public Task<ReadOnlyMemory<byte>?> GetAsync(string key, CancellationToken ct) => Task.FromResult<ReadOnlyMemory<byte>?>(null);

        public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class CountingProvider : PlainProvider, ICacheCounter
    {
        private readonly Dictionary<string, long> _counts = [];

        public IReadOnlyCollection<string> Keys => _counts.Keys;

        public Task<long> IncrementAsync(string key, long by, TimeSpan ttl, CancellationToken ct)
        {
            lock (_counts)
            {
                _counts[key] = _counts.GetValueOrDefault(key) + by;
                return Task.FromResult(_counts[key]);
            }
        }
    }
}
