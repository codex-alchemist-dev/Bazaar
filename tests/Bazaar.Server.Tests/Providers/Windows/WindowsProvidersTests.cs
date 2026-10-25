using Bazaar.Server.Providers.Windows;

namespace Bazaar.Server.Tests.Providers.Windows;

public class WindowsProvidersTests
{
    [Fact]
    public void Service_host_candidates_hold_the_windows_service_on_windows_and_nothing_elsewhere()
    {
        var candidates = WindowsProviders.ServiceHostCandidates();

        if (OperatingSystem.IsWindows())
        {
            var candidate = Assert.Single(candidates);
            Assert.Equal("windows_service", candidate.Kind);
        }
        else
        {
            Assert.Empty(candidates);
        }
    }
}
