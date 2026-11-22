using System.Security.Cryptography;
using System.Text;
using Bazaar.Core;
using Bazaar.Server.Settings;
using Bazaar.Server.Vms.Android.Sdk;
using Bazaar.Server.Vms.Devices;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Vms.Android;

/// <summary>
/// The Android emulator provider as a Bazaar Component (doc 62 sections 5.1, 9.1 and 9.2). It registers one entry as an
/// <see cref="IDeviceProviderCatalogEntry"/>, so <c>vms.devices.list</c> can report it, and as an <see cref="IVmBackendSource"/>, so the
/// lifecycle can create and start Android guests on it. Registrations only: nothing is read from the settings store, probed or started
/// until the listing or the lifecycle asks. The listing and the router keep their own <c>vms.devices.enabled</c> check, so a registered
/// but disabled provider takes no new guest.
/// </summary>
public sealed class AndroidComponent : IBazaarComponent
{
    /// <summary>Configuration section for the values that are not settings: <c>SystemImageVersion</c> (GUID, optional).</summary>
    public const string SectionName = "Bazaar:Vms:Android";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "vms-android",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["settings"],
        Provides: ["feature:vms-android"],
        SoftDependsOn: ["vms", "vms-devices"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Install-from-workspace needs the mounts module. Until it registers a real resolver this one refuses, so the call fails
        // loudly and nothing is read from a guessed folder.
        services.TryAddSingleton<IAndroidWorkspace, UnavailableAndroidWorkspace>();
        // A second AddServices call must not list the entry twice. A factory descriptor cannot go through TryAddEnumerable.
        if (services.All(d => d.ServiceType != typeof(AndroidCatalogEntry)))
        {
            services.AddSingleton(sp => new AndroidCatalogEntry(
                sp.GetRequiredService<ISettingStore>(),
                configuration,
                sp.GetRequiredService<IAndroidWorkspace>(),
                clock: sp.GetService<TimeProvider>(),
                logger: sp.GetService<ILoggerFactory>()?.CreateLogger<AndroidCatalogEntry>()));
            services.AddSingleton<IDeviceProviderCatalogEntry>(sp => sp.GetRequiredService<AndroidCatalogEntry>());
            services.AddSingleton<IVmBackendSource>(sp => sp.GetRequiredService<AndroidCatalogEntry>());
        }
    }
}

/// <summary>An <see cref="IAndroidWorkspace"/> for hosts with no mounts module: installing from a workspace is unavailable.</summary>
public sealed class UnavailableAndroidWorkspace : IAndroidWorkspace
{
    public string ResolveFile(Guid vmId, string relativePath) =>
        throw new NotSupportedException("Installing from a workspace needs the mounts module, which is not registered on this host.");
}

/// <summary>
/// The catalogue entry and backend source of the Android provider (doc 62 sections 9.1 and 9.2). It builds the real backend only when
/// the SDK root, AVD home and system image are all set and valid; otherwise it reports Usable=false with a plain reason and never throws.
/// Settings are cached for a few seconds (<see cref="DeviceProviderEntry{TBackend}"/>): <c>vms.android.max_concurrent</c> is live and
/// reaches the same backend on the next refresh; the folders and the image are read again only while no backend exists, and need a
/// restart after that.
/// </summary>
public sealed class AndroidCatalogEntry : DeviceProviderEntry<AndroidBackend>
{
    private readonly ISettingStore _settings;
    private readonly IConfiguration _configuration;
    private readonly IAndroidWorkspace _workspace;
    private readonly Func<AndroidOptions, IAndroidWorkspace, AndroidBackend> _factory;

    public AndroidCatalogEntry(
        ISettingStore settings,
        IConfiguration configuration,
        IAndroidWorkspace workspace,
        Func<AndroidOptions, IAndroidWorkspace, AndroidBackend>? factory = null,
        TimeProvider? clock = null,
        TimeSpan? refreshInterval = null,
        ILogger? logger = null)
        : base(clock, refreshInterval, logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _factory = factory ?? CreateReal;
    }

    public override string Kind => AndroidModule.Kind;

    public override IReadOnlyList<VmOs> GuestOs { get; } = [VmOs.Android];

    protected override DeviceProviderCapabilities DefaultCapabilities => AndroidModule.Capabilities;

    protected override string ReadFailureReason => "The Android settings could not be read.";

    /// <summary>The golden image id that stands for the pinned system image: configured, or derived from the package id.</summary>
    public static Guid ImageVersion(IConfiguration configuration, string package)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (Guid.TryParse(configuration.GetSection(AndroidComponent.SectionName)["SystemImageVersion"], out var configured))
        {
            return configured;
        }

        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("bazaar:android:" + package)).AsSpan(0, 16));
    }

    protected override async Task<IReadOnlyDictionary<string, string>> ReadInstallValuesAsync(CancellationToken ct) =>
        InstallValues(
            await _settings.GetAsync(DeviceProvidersSettings.AndroidSdkRoot, cancellationToken: ct).ConfigureAwait(false),
            await _settings.GetAsync(DeviceProvidersSettings.AndroidAvdHome, cancellationToken: ct).ConfigureAwait(false),
            await _settings.GetAsync(DeviceProvidersSettings.AndroidSystemImage, cancellationToken: ct).ConfigureAwait(false));

    private static Dictionary<string, string> InstallValues(string sdkRoot, string avdHome, string image) => new(StringComparer.Ordinal)
    {
        [DeviceProvidersSettings.AndroidSdkRoot.Name] = sdkRoot,
        [DeviceProvidersSettings.AndroidAvdHome.Name] = avdHome,
        [DeviceProvidersSettings.AndroidSystemImage.Name] = image,
    };

    protected override async Task<(AndroidBackend? Backend, string? Reason)> BuildAsync(CancellationToken ct)
    {
        var sdkRoot = await _settings.GetAsync(DeviceProvidersSettings.AndroidSdkRoot, cancellationToken: ct).ConfigureAwait(false);
        var avdHome = await _settings.GetAsync(DeviceProvidersSettings.AndroidAvdHome, cancellationToken: ct).ConfigureAwait(false);
        var image = await _settings.GetAsync(DeviceProvidersSettings.AndroidSystemImage, cancellationToken: ct).ConfigureAwait(false);
        var max = await _settings.GetAsync(DeviceProvidersSettings.AndroidMaxConcurrent, cancellationToken: ct).ConfigureAwait(false);

        var missing = new List<string>();
        if (sdkRoot.Length == 0)
        {
            missing.Add(DeviceProvidersSettings.AndroidSdkRoot.Name);
        }

        if (avdHome.Length == 0)
        {
            missing.Add(DeviceProvidersSettings.AndroidAvdHome.Name);
        }

        if (image.Length == 0)
        {
            missing.Add(DeviceProvidersSettings.AndroidSystemImage.Name);
        }

        if (missing.Count > 0)
        {
            return (null, $"Not configured: {string.Join(", ", missing)}.");
        }

        try
        {
            var options = new AndroidOptions
            {
                SdkRoot = sdkRoot,
                AvdHome = avdHome,
                SystemImagePackage = image,
                SystemImageVersion = ImageVersion(_configuration, image),
                MaxConcurrent = max,
            };
            var backend = _factory(options, _workspace);
            RecordBuiltFrom(InstallValues(sdkRoot, avdHome, image));
            return (backend, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // The validators name the setting and never a path.
            return (null, ex.Message);
        }
    }

    protected override async Task<bool> ApplyLiveAsync(AndroidBackend backend, CancellationToken ct)
    {
        var max = await _settings.GetAsync(DeviceProvidersSettings.AndroidMaxConcurrent, cancellationToken: ct).ConfigureAwait(false);
        if (max == backend.MaxConcurrentInstances)
        {
            return false;
        }

        backend.SetMaxConcurrent(max);
        return true;
    }

    private static AndroidBackend CreateReal(AndroidOptions options, IAndroidWorkspace workspace) =>
        new(new SdkAndroidHost(options, new ProcessAndroidCommandRunner()), new SdkAndroidHostFacts(options), workspace, options);
}
