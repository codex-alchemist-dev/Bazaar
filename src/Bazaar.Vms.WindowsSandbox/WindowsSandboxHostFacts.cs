using System.Runtime.InteropServices;
using Bazaar.Contracts.Modules;

namespace Bazaar.Vms.WindowsSandbox;

/// <summary>
/// Probes this host for the facts a Windows Sandbox module needs, without elevation (module host requirements, doc 50 section 5).
/// The sandbox feature is reported only when its executable is present, so a host where it is off, or where the probe cannot
/// tell, fails closed. The elevated feature-state check (spike S-WSB) can refine this later; it is not required to run.
/// </summary>
public sealed class WindowsSandboxHostFacts(Func<string, bool>? fileExists = null, Func<string>? systemDirectory = null) : IHostFactsSource
{
    /// <summary>Executable that exists only when the Windows Sandbox optional feature is installed.</summary>
    public const string SandboxExecutable = "WindowsSandbox.exe";

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
        if (os == "windows" && _fileExists(Path.Combine(_systemDirectory(), SandboxExecutable)))
        {
            features.Add("windows-sandbox");
        }

        return new HostFacts(os, arch, features);
    }
}
