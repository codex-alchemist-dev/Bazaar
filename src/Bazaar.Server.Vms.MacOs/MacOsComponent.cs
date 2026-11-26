using System.Security.Cryptography;
using System.Text;
using Bazaar.Core;
using Bazaar.Server.Settings;
using Bazaar.Server.Vms.Devices;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;
using Bazaar.Server.Vms.MacNode;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Vms.MacOs;

/// <summary>
/// The macOS VM provider as a Bazaar Component (doc 62 sections 5.3, 6, 9.1 and 9.2). It registers one entry as an
/// <see cref="IDeviceProviderCatalogEntry"/> so <c>vms.devices.list</c> can report it, and as an <see cref="IVmBackendSource"/> so the
/// lifecycle can create and start macOS guests on it. Off by default: it is in the host's component list, but the listing and the
/// router keep their own <c>vms.devices.enabled</c> check, and nothing is read, probed or started until one of them asks.
/// <para>
/// Apple rule: macOS guests run only on Apple hardware, through a Mac node, with at most two virtual instances per Mac. With no node
/// link registered, this component registers an <see cref="UnlinkedMacNodeLink"/>, and the entry reports not usable with the reason
/// "no Mac node linked".
/// </para>
/// </summary>
public sealed class MacOsComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "vms-macos",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["settings"],
        Provides: ["feature:vms-macos"],
        SoftDependsOn: ["vms", "vms-devices"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // A module that supplies the real link (over Conduit, doc 39) registers it first. Until then there is no Mac node.
        services.TryAddSingleton<IMacNodeLink, UnlinkedMacNodeLink>();
        // A second AddServices call must not list the entry twice. A factory descriptor cannot go through TryAddEnumerable.
        if (services.All(d => d.ServiceType != typeof(MacOsCatalogEntry)))
        {
            services.AddSingleton(sp => new MacOsCatalogEntry(
                sp.GetRequiredService<ISettingStore>(),
                sp.GetRequiredService<IMacNodeLink>(),
                clock: sp.GetService<TimeProvider>(),
                logger: sp.GetService<ILoggerFactory>()?.CreateLogger<MacOsCatalogEntry>()));
            services.AddSingleton<IDeviceProviderCatalogEntry>(sp => sp.GetRequiredService<MacOsCatalogEntry>());
            services.AddSingleton<IVmBackendSource>(sp => sp.GetRequiredService<MacOsCatalogEntry>());
        }
    }
}

/// <summary>
/// The catalogue entry and backend source of the macOS provider (doc 62 sections 9.1 and 9.2). The backend is built as soon as the
/// configuration is valid, whether or not a Mac node is linked; the node's state decides <c>Usable</c> on every probe, so a node that
/// connects later makes the provider usable without a restart. <c>vms.macos.max_concurrent</c> is live and never above Apple's two.
/// Never throws.
/// </summary>
public sealed class MacOsCatalogEntry : DeviceProviderEntry<MacOsBackend>
{
    private readonly ISettingStore _settings;
    private readonly IMacNodeLink _link;
    private readonly Func<MacOsOptions, IMacNodeLink, MacOsBackend> _factory;

    public MacOsCatalogEntry(
        ISettingStore settings,
        IMacNodeLink link,
        Func<MacOsOptions, IMacNodeLink, MacOsBackend>? factory = null,
        TimeProvider? clock = null,
        TimeSpan? refreshInterval = null,
        ILogger? logger = null)
        : base(clock, refreshInterval, logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _link = link ?? throw new ArgumentNullException(nameof(link));
        _factory = factory ?? ((options, l) => new MacOsBackend(l, options));
    }

    public override string Kind => MacOsModule.Kind;

    public override IReadOnlyList<VmOs> GuestOs { get; } = [VmOs.MacOs];

    protected override DeviceProviderCapabilities DefaultCapabilities => MacOsModule.Capabilities;

    protected override string ReadFailureReason => "The macOS settings could not be read.";

    /// <summary>The golden image id that stands for the pinned restore image, derived from its id, build and hash.</summary>
    public static Guid RestoreImageVersion(string id, string build, string sha256)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(sha256);
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"bazaar:macos:{id}:{build}:{sha256}")).AsSpan(0, 16));
    }

    protected override async Task<(MacOsBackend? Backend, string? Reason)> BuildAsync(CancellationToken ct)
    {
        var max = await _settings.GetAsync(DeviceProvidersSettings.MacOsMaxConcurrent, cancellationToken: ct).ConfigureAwait(false);
        var pin = await _settings.GetAsync(DeviceProvidersSettings.MacOsRestoreImagePin, cancellationToken: ct).ConfigureAwait(false);
        try
        {
            var (id, build, sha256) = SplitPin(pin);
            var options = new MacOsOptions
            {
                RestoreImageId = id,
                RestoreImageBuild = build,
                RestoreImageSha256 = sha256,
                RestoreImageVersion = RestoreImageVersion(id, build, sha256),
                MaxConcurrent = max,
            };
            return (_factory(options, _link), null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // The validators name the field and never a value.
            return (null, ex.Message);
        }
    }

    /// <summary>Splits the <c>vms.macos.restore_image_pin</c> value, <c>id:build:sha256</c>. Empty is an unset pin. Never echoes the value.</summary>
    private static (string Id, string Build, string Sha256) SplitPin(string pin)
    {
        if (pin.Length == 0)
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        var parts = pin.Split(':');
        if (parts.Length != 3)
        {
            throw new ArgumentException("vms.macos.restore_image_pin must be id:build:sha256.");
        }

        return (parts[0], parts[1], parts[2]);
    }

    protected override async Task<bool> ApplyLiveAsync(MacOsBackend backend, CancellationToken ct)
    {
        var max = await _settings.GetAsync(DeviceProvidersSettings.MacOsMaxConcurrent, cancellationToken: ct).ConfigureAwait(false);
        if (max == backend.MaxConcurrentInstances)
        {
            return false;
        }

        backend.SetMaxConcurrent(max);
        return true;
    }
}
