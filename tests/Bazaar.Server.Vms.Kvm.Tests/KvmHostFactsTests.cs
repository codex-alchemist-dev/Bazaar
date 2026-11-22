namespace Bazaar.Server.Vms.Kvm.Tests;

public sealed class KvmHostFactsTests
{
    [Fact]
    public void The_feature_is_reported_only_when_the_kvm_device_exists()
    {
        var features = new KvmHostFacts(path => path == KvmHostFacts.KvmDevice).Current().Features;

        if (OperatingSystem.IsLinux())
        {
            Assert.Contains("kvm", features);
        }
        else
        {
            Assert.DoesNotContain("kvm", features);
        }
    }

    [Fact]
    public void A_missing_device_fails_closed()
    {
        Assert.DoesNotContain("kvm", new KvmHostFacts(_ => false).Current().Features);
    }

    [Fact]
    public void A_non_linux_host_reports_no_kvm_feature_even_when_the_device_path_exists()
    {
        var features = new KvmHostFacts(_ => true).Current().Features;

        if (!OperatingSystem.IsLinux())
        {
            Assert.DoesNotContain("kvm", features);
        }
    }
}
