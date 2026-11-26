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
    public void Auto_selection_probes_systemd_on_linux_and_has_no_candidate_elsewhere()
    {
        var selection = ProviderSelector.Select(ProviderRole.ServiceHost, ProviderSelector.Auto, LinuxProviders.ServiceHostCandidates());

        if (OperatingSystem.IsLinux())
        {
            var status = Assert.Single(selection.Statuses);
            Assert.Equal("systemd", status.Kind);
            Assert.Equal(status.Usable, selection.Provider is not null);
        }
        else
        {
            Assert.Null(selection.Provider);
            Assert.Empty(selection.Statuses);
        }
    }
}
