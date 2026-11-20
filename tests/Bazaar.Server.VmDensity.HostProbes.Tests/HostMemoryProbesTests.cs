using Bazaar.Server.VmDensity.HostProbes;

namespace Bazaar.Server.VmDensity.HostProbes.Tests;

public class HostMemoryProbesTests
{
    [Fact]
    public void CreatesProbeForCurrentOs()
    {
        if (OperatingSystem.IsLinux())
        {
            Assert.IsType<LinuxHostMemoryProbe>(HostMemoryProbes.CreateForCurrentOs());
        }
        else if (OperatingSystem.IsWindows())
        {
            Assert.IsType<WindowsHostMemoryProbe>(HostMemoryProbes.CreateForCurrentOs());
        }
        else
        {
            Assert.Throws<PlatformNotSupportedException>(HostMemoryProbes.CreateForCurrentOs);
        }
    }
}
