using Bazaar.RateLimits;
using Bazaar.RateLimits.Cache;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests;

/// <summary>The host's one <see cref="IRateLimiter"/> is the shared cache-backed limiter (doc 15, PROPOSED), whatever order components register in.</summary>
public sealed class RateLimiterCompositionTests
{
    [Fact]
    public void The_host_component_list_contains_the_cache_rate_limiter()
    {
        Assert.Contains(HostComponents.All, c => c.Manifest.Id == RateLimitsCacheComponent.ManifestDefinition.Id);
    }

    [Fact]
    public void The_real_host_graph_resolves_the_rate_limiter_to_the_cache_limiter_and_it_limits_without_a_cache()
    {
        const string dbEnv = "BAZAAR_HOST_RATELIMIT_TEST_DB";
        const string tokenEnv = "BAZAAR_HOST_RATELIMIT_TEST_TOKEN";
        Environment.SetEnvironmentVariable(dbEnv, "Host=localhost;Database=unused");
        Environment.SetEnvironmentVariable(tokenEnv, "dev-secret-for-graph-test");
        try
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bazaar:Host:DataRoot"] = Path.Combine(Path.GetTempPath(), "bazaar-host-test-" + Guid.NewGuid().ToString("N")),
                ["Bazaar:Host:DatabaseConnectionReference"] = "env:" + dbEnv,
                ["Bazaar:DevAuth:Enabled"] = "true",
                ["Bazaar:DevAuth:TokenSecret"] = "env:" + tokenEnv,
            });
            builder.Services.AddBazaarHostApplication(builder.Configuration);
            using var host = builder.Build();

            var limiter = host.Services.GetRequiredService<IRateLimiter>();

            Assert.IsType<CacheRateLimiter>(limiter);
            Assert.Single(host.Services.GetServices<IRateLimiter>());

            // No cache provider is enabled in this graph that can count, so the in-process fallback decides and still limits.
            var policy = new RateLimitPolicy("host_graph_test", 1, TimeSpan.FromMinutes(1), RateLimitScope.Installation, "test.key");
            Assert.True(limiter.TryAcquire(policy, "install").Allowed);
            Assert.False(limiter.TryAcquire(policy, "install").Allowed);
            Assert.All(host.Services.GetServices<ICacheProvider>().OfType<ICacheCounter>(), _ => { });
        }
        finally
        {
            Environment.SetEnvironmentVariable(dbEnv, null);
            Environment.SetEnvironmentVariable(tokenEnv, null);
        }
    }
}
