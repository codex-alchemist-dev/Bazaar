using Bazaar.Contracts.Modules;
using Bazaar.Server.Vms.Devices;

namespace Bazaar.Server.Vms.Android;

/// <summary>
/// The only seam to the Android SDK tools. Cloud CI uses a fake; the real adapter (avdmanager, emulator, one private adb server per VM)
/// is a separate work package and a gated integration category (doc 62 section 10, WP4).
/// </summary>
public interface IAndroidHost
{
    Task CreateAvdAsync(string avdName, string systemImagePackage, CancellationToken ct);

    Task DeleteAvdAsync(string avdName, CancellationToken ct);

    /// <summary>Starts a headless emulator on a console port, optionally from a snapshot.</summary>
    Task StartEmulatorAsync(string avdName, int port, string? loadSnapshot, CancellationToken ct);

    Task StopEmulatorAsync(int port, bool force, CancellationToken ct);

    Task<bool> IsRunningAsync(int port, CancellationToken ct);

    Task SaveSnapshotAsync(int port, string snapshotName, CancellationToken ct);

    Task InstallAsync(int port, string apkPath, CancellationToken ct);

    Task LaunchAsync(int port, string appId, CancellationToken ct);

    Task<byte[]> ScreencapAsync(int port, CancellationToken ct);

    Task InputAsync(int port, DeviceInput input, CancellationToken ct);
}

/// <summary>Probes whether the SDK tools and acceleration exist. Synchronous and read-only.</summary>
public interface IAndroidHostFacts
{
    HostFacts Current();
}
