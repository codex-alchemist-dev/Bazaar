using System.Runtime.InteropServices;
using Bazaar.Contracts.Modules;

namespace Bazaar.Server.Vms.Kvm;

/// <summary>
/// Probes this host for the facts the KVM module needs, without elevation. The <c>kvm</c> feature is reported only on Linux when
/// the KVM device node exists, so a host without hardware virtualisation, a non-Linux host, or a host where the probe cannot
/// tell, fails closed.
/// </summary>
public sealed class KvmHostFacts(Func<string, bool>? fileExists = null) : IHostFactsSource
{
    /// <summary>Device node that exists only when the kernel KVM module is loaded and usable.</summary>
    public const string KvmDevice = "/dev/kvm";

    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;

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
        if (os == "linux" && _fileExists(KvmDevice))
        {
            features.Add(KvmModule.Kind);
        }

        return new HostFacts(os, arch, features);
    }
}
