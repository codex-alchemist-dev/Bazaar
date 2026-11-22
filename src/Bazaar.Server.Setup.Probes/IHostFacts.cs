using System.Net.NetworkInformation;

namespace Bazaar.Server.Setup.Probes;

/// <summary>The read-only facts about this machine the preflight probes need (doc 45 section 1.12). Tests fake it.</summary>
public interface IHostFacts
{
    bool IsWindows { get; }

    bool IsLinux { get; }

    bool Is64Bit { get; }

    /// <summary>The platform default data root (doc 30: %ProgramData%\Bazaar on Windows, /var/lib/bazaar on Linux).</summary>
    string DefaultDataRoot { get; }

    bool DirectoryExists(string path);

    /// <summary>True when the directory carries the read-only attribute.</summary>
    bool IsMarkedReadOnly(string path);

    /// <summary>The TCP ports something on this machine already listens on.</summary>
    IReadOnlyCollection<int> ListeningTcpPorts();

    /// <summary>The volume that holds <paramref name="path"/> (its nearest existing folder), or null when it cannot be told.</summary>
    string? VolumeOf(string path);
}

/// <summary>The runtime's own answers. No OS-specific API: everything here is in the base class library.</summary>
public sealed class RuntimeHostFacts : IHostFacts
{
    /// <inheritdoc />
    public bool IsWindows => OperatingSystem.IsWindows();

    /// <inheritdoc />
    public bool IsLinux => OperatingSystem.IsLinux();

    /// <inheritdoc />
    public bool Is64Bit => Environment.Is64BitOperatingSystem;

    /// <inheritdoc />
    public string DefaultDataRoot => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Bazaar")
        : "/var/lib/bazaar";

    /// <inheritdoc />
    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <inheritdoc />
    public bool IsMarkedReadOnly(string path) => new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReadOnly);

    /// <inheritdoc />
    public IReadOnlyCollection<int> ListeningTcpPorts() =>
        [.. IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(endpoint => endpoint.Port).Distinct()];

    /// <inheritdoc />
    public string? VolumeOf(string path)
    {
        var full = Path.GetFullPath(path);
        while (!Directory.Exists(full))
        {
            var parent = Path.GetDirectoryName(full);
            if (parent is null)
            {
                return null;
            }

            full = parent;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string? best = null;
        foreach (var drive in DriveInfo.GetDrives())
        {
            var root = drive.RootDirectory.FullName;
            if (full.StartsWith(root, comparison) && (best is null || root.Length > best.Length))
            {
                best = root;
            }
        }

        return best;
    }
}
