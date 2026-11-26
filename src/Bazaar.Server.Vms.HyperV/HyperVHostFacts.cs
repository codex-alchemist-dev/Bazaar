using System.Runtime.InteropServices;
using Bazaar.Contracts.Modules;

namespace Bazaar.Server.Vms.HyperV;

/// <summary>
/// Probes this host for the facts the Hyper-V module needs, without elevation. The <c>hyperv</c> feature is reported only when
/// the virtual machine management service executable is present, so a host where the role is off, or where the probe cannot
/// tell, fails closed.
/// </summary>
public sealed class HyperVHostFacts(Func<string, bool>? fileExists = null, Func<string>? systemDirectory = null) : IHostFactsSource
{
    /// <summary>Executable that exists only when the Hyper-V role is installed.</summary>
    public const string ManagementServiceExecutable = "vmms.exe";

    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;
    private readonly Func<string> _systemDirectory = systemDirectory ?? (() => Environment.SystemDirectory);

    /// <inheritdoc />
    public HostFacts Current()
    {
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "other";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => "other",
        };

        var features = new HashSet<string>(StringComparer.Ordinal);
        if (os == "windows" && _fileExists(Path.Combine(_systemDirectory(), ManagementServiceExecutable)))
        {
            features.Add("hyperv");
        }

        return new HostFacts(os, arch, features);
    }
}
