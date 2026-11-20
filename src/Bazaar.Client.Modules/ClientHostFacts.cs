using System.Runtime.InteropServices;
using Bazaar.Contracts.Modules;

namespace Bazaar.Client.Modules;

/// <summary>What the machine this client runs on offers, for each module's host requirements (doc 50 section 5).</summary>
public static class ClientHostFacts
{
    /// <summary>The OS and CPU architecture of this process. A client host offers none of the server-side features.</summary>
    public static HostFacts Current() => new(CurrentOperatingSystem(), CurrentArchitecture(), new HashSet<string>(StringComparer.Ordinal));

    /// <summary>The OS name a manifest uses, or <c>unknown</c> on a platform no client ships on.</summary>
    internal static string CurrentOperatingSystem() =>
        OperatingSystem.IsAndroid() ? "android"
        : OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsLinux() ? "linux"
        : "unknown";

    /// <summary>The CPU architecture name a manifest uses, or <c>unknown</c>.</summary>
    internal static string CurrentArchitecture() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => "unknown",
    };
}
