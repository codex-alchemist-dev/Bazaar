using Bazaar.Server.VmDensity.Abstractions;

namespace Bazaar.Server.VmDensity.HostProbes;

/// <summary>Chooses the host memory probe for the operating system the server runs on.</summary>
public static class HostMemoryProbes
{
    /// <summary>Creates the Linux or Windows probe.</summary>
    /// <exception cref="PlatformNotSupportedException">On any other operating system.</exception>
    public static IHostMemoryProbe CreateForCurrentOs()
    {
        if (OperatingSystem.IsLinux())
        {
            return new LinuxHostMemoryProbe();
        }

        if (OperatingSystem.IsWindows())
        {
            return new WindowsHostMemoryProbe();
        }

        throw new PlatformNotSupportedException("No host memory probe exists for this operating system.");
    }
}
