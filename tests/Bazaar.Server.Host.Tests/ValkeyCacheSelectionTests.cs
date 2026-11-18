using Bazaar.Server.Host.Composition;
using Bazaar.Server.Providers;
using Bazaar.Server.Valkey;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBazaarValkey(configuration);
        services.AddBazaarProviders(configuration);
        await using var provider = services.BuildServiceProvider();

        // No hosted service has started the child, so the window closes with nothing answering and the start goes on.
        var report = provider.GetRequiredService<ProviderCapabilityReport>();

        var cache = report.Selections.Single(s => s.Role == ProviderRole.Cache);
        Assert.Null(cache.Provider);
        Assert.Equal(ProviderSelector.Auto, cache.Requested);
        Assert.Contains(cache.Statuses, s => s.Kind == "valkey" && !s.Usable && s.Reason == "managed server did not answer within 1 seconds of start");
    }
}
