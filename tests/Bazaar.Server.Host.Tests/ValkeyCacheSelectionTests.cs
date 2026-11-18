using Bazaar.Server.Host.Composition;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Providers;
using Bazaar.Server.Valkey;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Host.Tests;

/// <summary>An explicit managed Valkey cache at startup: an expired start window leaves the cache out and does not fail the start.</summary>
public sealed class ValkeyCacheSelectionTests
{
    [Fact]
    public async Task An_explicit_managed_cache_that_never_answers_is_left_out_instead_of_failing_the_start()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:Providers:ServiceHost"] = "none",
            ["Bazaar:Providers:Cache"] = "valkey",
            ["Bazaar:Cache:Valkey:Managed"] = "true",
            ["Bazaar:Cache:Valkey:StartWindowSeconds"] = "1",
        }).Build();
        var state = new HostState(TimeProvider.System);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(state);
        services.AddBazaarValkey(configuration);
        services.AddBazaarProviders(configuration);
        services.AddBazaarCacheSelection();
        await using var provider = services.BuildServiceProvider();
        var report = provider.GetRequiredService<ProviderCapabilityReport>();

        // Before the step runs, the cache is pending and nothing has been probed.
        Assert.True(report.Selections.Single(s => s.Role == ProviderRole.Cache).IsPending);

        // The host has finished the instance lock. The supervisor is not started here, so the child never answers and the window closes.
        state.MarkStarted();
        var step = provider.GetServices<IHostedService>().OfType<CacheSelectionStep>().Single();
        await step.StartAsync(TestContext.Current.CancellationToken);

        var cache = report.Selections.Single(s => s.Role == ProviderRole.Cache);
        Assert.Null(cache.Provider);
        Assert.Equal("valkey", cache.Requested);
        Assert.Contains(cache.Statuses, s => s.Kind == "valkey" && !s.Usable && s.Reason == "managed server did not answer within 1 seconds of start");
    }
}
