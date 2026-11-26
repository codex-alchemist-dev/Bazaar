using Bazaar.Contracts;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Providers;
using Bazaar.Server.WebAdmin;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests;

public class HostAdminStatusSourceTests
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Reports_version_uptime_and_non_secret_lines()
    {
        var time = new FixedTime(DateTimeOffset.UnixEpoch);
        var state = new HostState(time);
        time.Now = DateTimeOffset.UnixEpoch.AddSeconds(90);

        var view = await new HostAdminStatusSource(state, new ProviderCapabilityReport([])).GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Bazaar", view.DisplayName);
        Assert.Equal(ProductVersion.Informational, view.Version);
        Assert.Matches(@"^\d+\.\d+\.\d+", view.Version);
        Assert.Equal(DateTimeOffset.UnixEpoch, view.StartedAt);
        Assert.Equal(90, view.UptimeSeconds);
        Assert.Null(view.Codename);
        Assert.Contains(view.ProviderLines, l => l.StartsWith("os: ", StringComparison.Ordinal));
        Assert.Contains(view.ProviderLines, l => l.StartsWith("runtime: ", StringComparison.Ordinal));
    }

    [Fact]
    public void Is_registered_by_the_host_runtime()
    {
        var services = new ServiceCollection();
        services.AddBazaarHostRuntime(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        Assert.Contains(services, d => d.ServiceType == typeof(IAdminStatusSource) && d.ImplementationType == typeof(HostAdminStatusSource));
    }
}
