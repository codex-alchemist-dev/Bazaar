using System.Runtime.InteropServices;

namespace Bazaar.Diagnostics.Capture;

/// <summary>Operating system, runtime and CPU information. No host name, user name or addresses.</summary>
/// <param name="OsDescription">The operating system description.</param>
/// <param name="RuntimeDescription">The runtime description, for example <c>.NET 11.0.0</c>.</param>
/// <param name="Architecture">The process architecture, lower case, for example <c>x64</c>.</param>
public sealed record DeviceInfo(string OsDescription, string RuntimeDescription, string Architecture);

/// <summary>A module the caller reports, with its version. Modules are never found by reflection over arbitrary assemblies.</summary>
/// <param name="Name">The module name.</param>
/// <param name="Version">The module version string.</param>
public sealed record ModuleInfo(string Name, string Version);

/// <summary>The client's identity for a report: app version, device and modules.</summary>
/// <param name="AppVersion">The client version string, for example <c>0.1.0+a1b2c3d</c>.</param>
/// <param name="Device">The device information.</param>
/// <param name="Modules">The modules, sorted by name.</param>
public sealed record ClientInfo(string AppVersion, DeviceInfo Device, IReadOnlyList<ModuleInfo> Modules);

/// <summary>Supplies <see cref="ClientInfo"/>. Tests inject a fixed value so that captures are deterministic.</summary>
public interface IClientInfoProvider
{
    /// <summary>Returns the client information as it is now.</summary>
    ClientInfo Current();
}

/// <summary>
/// The production provider. The app version and module list come from the caller; the device fields come from
/// <see cref="RuntimeInformation"/>, which works the same on Windows and Linux.
/// </summary>
public sealed class RuntimeClientInfoProvider : IClientInfoProvider
{
    private readonly string _appVersion;
    private readonly IReadOnlyList<ModuleInfo> _modules;

    /// <param name="appVersion">The client version string.</param>
    /// <param name="modules">The loaded modules. Sorted by name here, so the output does not depend on the caller's order.</param>
    public RuntimeClientInfoProvider(string appVersion, IEnumerable<ModuleInfo> modules)
    {
        ArgumentException.ThrowIfNullOrEmpty(appVersion);
        ArgumentNullException.ThrowIfNull(modules);
        _appVersion = appVersion;
        _modules = modules
            .OrderBy(module => module.Name, StringComparer.Ordinal)
            .ThenBy(module => module.Version, StringComparer.Ordinal)
            .ToArray();
    }

    /// <inheritdoc/>
    public ClientInfo Current()
    {
        var device = new DeviceInfo(
            RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());
        return new ClientInfo(_appVersion, device, _modules);
    }
}
