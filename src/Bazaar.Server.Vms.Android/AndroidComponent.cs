using System.Security.Cryptography;
using System.Text;
using Bazaar.Core;
using Bazaar.Server.Settings;
using Bazaar.Server.Vms.Android.Sdk;
using Bazaar.Server.Vms.Devices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Vms.Android;

/// <summary>
/// The Android emulator provider as a Bazaar Component (doc 62 sections 5.1 and 9.1). It registers the provider as an
/// <see cref="IDeviceProviderCatalogEntry"/> so <c>vms.devices.list</c> can report it. Registrations only: nothing is read from the
/// settings store, probed or started until the list handler asks for an enabled kind. The listing keeps its own
/// <c>vms.devices.enabled</c> filter, so a registered but disabled provider is never touched.
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
            sp.GetRequiredService<IAndroidWorkspace>()));
            services.AddSingleton<IDeviceProviderCatalogEntry>(sp => sp.GetRequiredService<AndroidCatalogEntry>());
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
/// The catalogue entry of the Android provider. It reads <c>vms.android.*</c> on first use and builds the real backend only when
/// the SDK root, AVD home and system image are all set and valid. Otherwise it reports Usable=false with a plain reason and never
/// throws. The values are read once per process, so a changed setting needs a restart.
/// </summary>
public sealed class AndroidCatalogEntry : IDeviceProviderCatalogEntry, IDisposable
{
    private readonly ISettingStore _settings;
    private readonly IConfiguration _configuration;
    private readonly IAndroidWorkspace _workspace;
    private readonly Func<AndroidOptions, IAndroidWorkspace, AndroidBackend> _factory;
    private readonly Lock _gate = new();
    private State? _state;

    public AndroidCatalogEntry(
        ISettingStore settings,
        IConfiguration configuration,
        IAndroidWorkspace workspace,
        Func<AndroidOptions, IAndroidWorkspace, AndroidBackend>? factory = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _factory = factory ?? CreateReal;
    }

    /// <summary>The real backend, or null when a required setting is unset or invalid.</summary>
    public AndroidBackend? Backend => Resolve().Backend;

    public string Kind => AndroidModule.Kind;

    public DeviceProviderCapabilities Capabilities => Resolve().Backend?.Capabilities ?? AndroidModule.Capabilities;

    public int Running => Resolve().Backend?.Running ?? 0;

    public DeviceProbeResult ProbeDevice()
    {
        var state = Resolve();
        return state.Backend?.ProbeDevice() ?? new DeviceProbeResult(false, state.Reason);
    }

    public IReadOnlyList<DeviceGuestState> RunningGuests() => Resolve().Backend?.RunningGuests() ?? [];

    public void Dispose() => _state?.Backend?.Dispose();

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

    private static AndroidBackend CreateReal(AndroidOptions options, IAndroidWorkspace workspace) =>
        new(new SdkAndroidHost(options, new ProcessAndroidCommandRunner()), new SdkAndroidHostFacts(options), workspace, options);

    private State Resolve()
    {
        lock (_gate)
        {
            if (_state is not null)
            {
                return _state;
            }

            try
            {
                // The store is async and this entry is read synchronously; the read runs once on the pool.
                var state = Task.Run(ResolveAsync).GetAwaiter().GetResult();
                _state = state;
                return state;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Not cached: a store that is down now may answer on the next list.
                return new State(null, "The Android settings could not be read.");
            }
        }
    }

    private async Task<State> ResolveAsync()
    {
        var sdkRoot = await _settings.GetAsync(DeviceProvidersSettings.AndroidSdkRoot).ConfigureAwait(false);
        var avdHome = await _settings.GetAsync(DeviceProvidersSettings.AndroidAvdHome).ConfigureAwait(false);
        var image = await _settings.GetAsync(DeviceProvidersSettings.AndroidSystemImage).ConfigureAwait(false);
        var max = await _settings.GetAsync(DeviceProvidersSettings.AndroidMaxConcurrent).ConfigureAwait(false);

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
            return new State(null, $"Not configured: {string.Join(", ", missing)}.");
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
            return new State(_factory(options, _workspace), null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // The validators name the setting and never a path.
            return new State(null, ex.Message);
        }
    }

    private sealed record State(AndroidBackend? Backend, string? Reason);
}
