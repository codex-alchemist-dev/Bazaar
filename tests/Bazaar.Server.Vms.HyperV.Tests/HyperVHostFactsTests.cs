namespace Bazaar.Server.Vms.HyperV.Tests;

public sealed class HyperVHostFactsTests
{
    private const string _systemDirectory = @"C:\Windows\System32";

    [Fact]
    public void The_feature_is_reported_only_when_the_management_service_executable_exists()
    {
        var expected = Path.Combine(_systemDirectory, HyperVHostFacts.ManagementServiceExecutable);
        var features = new HyperVHostFacts(path => path == expected, () => _systemDirectory).Current().Features;

        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("hyperv", features);
        }
        else
        {
            Assert.DoesNotContain("hyperv", features);
        }
    }

    [Fact]
    public void A_missing_executable_fails_closed()
    {
        Assert.DoesNotContain("hyperv", new HyperVHostFacts(_ => false, () => _systemDirectory).Current().Features);
    }
}
