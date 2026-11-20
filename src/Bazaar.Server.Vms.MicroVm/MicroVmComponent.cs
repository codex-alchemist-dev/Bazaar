using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Contracts.Modules;
using Bazaar.Core;
using Bazaar.Server.Settings;
using Bazaar.Server.Vms.Devices;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;
using Bazaar.Server.Vms.MicroVm.Monitors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Vms.MicroVm;

/// <summary>
/// The microVM provider as a Bazaar Component (doc 62 sections 5.0 and 9.1). It registers the provider as an
/// <see cref="IDeviceProviderCatalogEntry"/> so <c>vms.devices.list</c> can report it, and as an <see cref="IVmBackendSource"/> so the
/// lifecycle can create and start microVMs on it (doc 62 section 9.2). Registrations only: nothing is read, probed or started until the
/// listing or the lifecycle asks, and both keep their own <c>vms.devices.enabled</c> check. A Linux guest goes to the microVM provider
/// only when the request names the kind; it is never chosen silently instead of the pair provider.
/// </summary>
public sealed class MicroVmComponent : IBazaarComponent
{
    /// <summary>
    /// Configuration section for the values that are not settings: <c>MonitorBinaryPath</c>, <c>KernelPath</c>, <c>RootfsPath</c>,
    /// <c>JailerPath</c>, <c>JailerUid</c>, <c>JailerGid</c>, <c>IpPath</c> and <c>ImageVersion</c> (GUID, optional).
    /// </summary>
    public const string SectionName = "Bazaar:Vms:MicroVm";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "vms-microvm",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["settings"],
        Provides: ["feature:vms-microvm"],
        SoftDependsOn: ["vms", "vms-devices"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // A second AddServices call must not list the entry twice. A factory descriptor cannot go through TryAddEnumerable.
        if (services.All(d => d.ServiceType != typeof(MicroVmCatalogEntry)))
        {
            services.AddSingleton(sp => new MicroVmCatalogEntry(
                sp.GetRequiredService<ISettingStore>(), configuration, clock: sp.GetService<TimeProvider>()));
            services.AddSingleton<IDeviceProviderCatalogEntry>(sp => sp.GetRequiredService<MicroVmCatalogEntry>());
            services.AddSingleton<IVmBackendSource>(sp => sp.GetRequiredService<MicroVmCatalogEntry>());
        }
    }
}

/// <summary>
/// Host facts of the microVM provider on Linux: <c>/dev/kvm</c> readable gives <c>kvm</c>, and the configured monitor binary existing
/// as well gives <c>microvm</c>. Any other OS reports no features, so the probe says not usable. Fails closed; probes are injected for tests.
/// </summary>
public sealed class LinuxMicroVmHostFacts : IMicroVmHostFacts
{
    private readonly MicroVmOptions _options;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, bool> _deviceReadable;
    private readonly string? _osOverride;

    public LinuxMicroVmHostFacts(
        MicroVmOptions options,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? deviceReadable = null,
        string? osOverride = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _fileExists = fileExists ?? File.Exists;
        _deviceReadable = deviceReadable ?? CanRead;
        _osOverride = osOverride;
    }

    public HostFacts Current()
    {
        var os = _osOverride ?? (OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "other");
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => "other",
        };

        var features = new HashSet<string>(StringComparer.Ordinal);
        if (os == "linux" && _deviceReadable("/dev/kvm"))
        {
            features.Add("kvm");
            if (_options.MonitorBinaryPath is { } binary && _fileExists(binary))
            {
                features.Add("microvm");
            }
        }

        return new HostFacts(os, arch, features);
    }

    private static bool CanRead(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// The catalogue entry and backend source of the microVM provider (doc 62 sections 9.1 and 9.2). It reads <c>vms.microvm.*</c> and
/// the <c>Bazaar:Vms:MicroVm</c> section and builds the real backend only when the state directory, monitor binary, kernel and root file
/// system are set and valid; otherwise it reports Usable=false with a plain reason and never throws. Settings are cached for a few
/// seconds (<see cref="DeviceProviderEntry{TBackend}"/>): <c>vms.microvm.max_concurrent</c> is live and reaches the same backend on the
/// next refresh; the other values are read again only while no backend exists, and need a restart after that.
/// </summary>
public sealed class MicroVmCatalogEntry : DeviceProviderEntry<MicroVmBackend>
{
    private static readonly string[] _ipCandidates = ["/usr/sbin/ip", "/sbin/ip", "/usr/bin/ip", "/bin/ip"];

    private readonly ISettingStore _settings;
    private readonly IConfiguration _configuration;
    private readonly Func<MicroVmOptions, MicroVmBackend> _factory;

    public MicroVmCatalogEntry(
        ISettingStore settings,
        IConfiguration configuration,
        Func<MicroVmOptions, MicroVmBackend>? factory = null,
        TimeProvider? clock = null,
        TimeSpan? refreshInterval = null)
        : base(clock, refreshInterval)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _factory = factory ?? CreateReal;
    }

    public override string Kind => MicroVmModule.Kind;

    public override IReadOnlyList<VmOs> GuestOs { get; } = [VmOs.Linux];

    protected override DeviceProviderCapabilities DefaultCapabilities { get; } = new(
        GuestOs: "linux",
        Isolation: DeviceIsolation.HardwareVirtualised,
        Save: MicroVmModule.Capabilities.Save,
        Checkpoint: MicroVmModule.Capabilities.Checkpoint,
        Restore: MicroVmModule.Capabilities.Restore,
        Headless: MicroVmModule.Capabilities.Headless,
        MaxConcurrentInstances: DeviceProvidersSettings.MicroVmMaxConcurrent.Default,
        GuestAgent: MicroVmModule.Capabilities.GuestAgent,
        EgressEnforced: MicroVmModule.Capabilities.EgressEnforced,
        ParentContentPinned: MicroVmModule.Capabilities.ParentContentPinned,
        ScreenSource: false,
        RemoteNode: false);

    protected override string ReadFailureReason => "The microVM settings could not be read.";

    protected override async Task<(MicroVmBackend? Backend, string? Reason)> BuildAsync(CancellationToken ct)
    {
        var stateDir = await _settings.GetAsync(DeviceProvidersSettings.MicroVmStateDir, cancellationToken: ct).ConfigureAwait(false);
        var monitor = await _settings.GetAsync(DeviceProvidersSettings.MicroVmMonitor, cancellationToken: ct).ConfigureAwait(false);
        var max = await _settings.GetAsync(DeviceProvidersSettings.MicroVmMaxConcurrent, cancellationToken: ct).ConfigureAwait(false);

        var section = _configuration.GetSection(MicroVmComponent.SectionName);
        var binary = Unset(section["MonitorBinaryPath"]);
        var kernel = Unset(section["KernelPath"]);
        var rootfs = Unset(section["RootfsPath"]);
        var jailer = Unset(section["JailerPath"]);

        var missing = new List<string>();
        if (stateDir.Length == 0)
        {
            missing.Add(DeviceProvidersSettings.MicroVmStateDir.Name);
        }

        foreach (var (name, value) in new[] { ("MonitorBinaryPath", binary), ("KernelPath", kernel), ("RootfsPath", rootfs) })
        {
            if (value is null)
            {
                missing.Add($"{MicroVmComponent.SectionName}:{name}");
            }
        }

        if (missing.Count > 0)
        {
            return (null, $"Not configured: {string.Join(", ", missing)}.");
        }

        try
        {
            var options = new MicroVmOptions
            {
                Monitor = monitor == MicroVmMonitorSetting.CloudHypervisor ? MicroVmMonitorKind.CloudHypervisor : MicroVmMonitorKind.Firecracker,
                ImageVersion = ImageVersion(section["ImageVersion"], kernel!, rootfs!),
                StateDirectory = stateDir,
                MaxConcurrent = max,
                MonitorBinaryPath = binary,
                KernelPath = kernel,
                RootfsPath = rootfs,
                JailerPath = jailer,
                JailerUid = int.TryParse(section["JailerUid"], out var uid) ? uid : 0,
                JailerGid = int.TryParse(section["JailerGid"], out var gid) ? gid : 0,
            };
            return (_factory(options), null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // The validators name the setting and never a path.
            return (null, ex.Message);
        }
    }

    protected override async Task<bool> ApplyLiveAsync(MicroVmBackend backend, CancellationToken ct)
    {
        var max = await _settings.GetAsync(DeviceProvidersSettings.MicroVmMaxConcurrent, cancellationToken: ct).ConfigureAwait(false);
        if (max == backend.MaxConcurrentInstances)
        {
            return false;
        }

        backend.SetMaxConcurrent(max);
        return true;
    }

    private static MicroVmBackend CreateReal(MicroVmOptions options)
    {
        var launcher = new ProcessMicroVmLauncher();
        var ip = _ipCandidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("The ip tool was not found.");
        IMicroVmMonitor monitor = options.Monitor switch
        {
            MicroVmMonitorKind.CloudHypervisor => new CloudHypervisorMonitor(options, new UnixSocketApiClient(), launcher, new IpTapNetwork(ip, launcher)),
            _ => new FirecrackerMonitor(options, new UnixSocketApiClient(), launcher, new IpTapNetwork(ip, launcher)),
        };
        return new MicroVmBackend(monitor, new LinuxMicroVmHostFacts(options), options);
    }

    private static string? Unset(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static Guid ImageVersion(string? configured, string kernel, string rootfs) =>
        Guid.TryParse(configured, out var id)
            ? id
            : new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"bazaar:microvm:{kernel}:{rootfs}")).AsSpan(0, 16));
}
