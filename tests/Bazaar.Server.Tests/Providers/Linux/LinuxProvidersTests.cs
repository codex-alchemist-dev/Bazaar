using Bazaar.Server.Providers;
using Bazaar.Server.Providers.Linux;

namespace Bazaar.Server.Tests.Providers.Linux;

public class LinuxProvidersTests
{
    [Fact]
    public void ServiceHostCandidates_offers_systemd_on_linux_and_nothing_elsewhere()
    {
        var candidates = LinuxProviders.ServiceHostCandidates();

        if (OperatingSystem.IsLinux())
        {
            var candidate = Assert.Single(candidates);
            Assert.IsType<SystemdServiceHostProvider>(candidate);
            Assert.Equal("systemd", candidate.Kind);
        }
        else
        {
            Assert.Empty(candidates);
        }
    }

    [Fact]
    public void Report_lists_systemd_with_its_own_status_on_linux_and_nothing_elsewhere()
    {
        var report = new ProviderCapabilityReport(LinuxProviders.ServiceHostCandidates());

        if (OperatingSystem.IsLinux())
        {
            var entry = Assert.Single(report.Entries);
            Assert.Equal(ProviderRole.ServiceHost, entry.Role);
            Assert.Equal("systemd", entry.Kind);
        }
        else
        {
            Assert.Empty(report.Entries);
        }
    }
}
