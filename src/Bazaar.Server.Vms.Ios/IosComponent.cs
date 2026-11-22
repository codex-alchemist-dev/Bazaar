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

namespace Bazaar.Server.Vms.Ios;

/// <summary>
/// The iOS Simulator provider as a Bazaar Component (doc 62 sections 5.2, 6, 9.1 and 9.2). It registers one entry as an
/// <see cref="IDeviceProviderCatalogEntry"/> so <c>vms.devices.list</c> can report it, and as an <see cref="IVmBackendSource"/> so the
/// lifecycle can create and start iOS guests on it. Off by default: it is in the host's component list, but the listing and the router
/// keep their own <c>vms.devices.enabled</c> check, and nothing is read, probed or started until one of them asks.
/// <para>
/// Apple rule: iOS guests run only on Apple hardware, through a Mac node. With no node link registered, this component registers an
/// <see cref="UnlinkedMacNodeLink"/>, and the entry reports not usable with the reason "no Mac node linked".
/// </para>
/// </summary>
public sealed class IosComponent : IBazaarComponent
{
    /// <summary>Configuration section for the values that are not settings: <c>RuntimeId</c>, <c>RuntimeBuild</c>, <c>RuntimeVersion</c> (GUID, optional) and <c>DeviceType</c>.</summary>
    public const string SectionName = "Bazaar:Vms:Ios";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "vms-ios",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["settings"],
        Provides: ["feature:vms-ios"],
        SoftDependsOn: ["vms", "vms-devices"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // A module that supplies the real link (over Conduit, doc 39) registers it first. Until then there is no Mac node.
        services.TryAddSingleton<IMacNodeLink, UnlinkedMacNodeLink>();
        // A second AddServices call must not list the entry twice. A factory descriptor cannot go through TryAddEnumerable.
        if (services.All(d => d.ServiceType != typeof(IosCatalogEntry)))
        {
            services.AddSingleton(sp => new IosCatalogEntry(
                sp.GetRequiredService<ISettingStore>(),
                configuration,
                sp.GetRequiredService<IMacNodeLink>(),
                clock: sp.GetService<TimeProvider>(),
                logger: sp.GetService<ILoggerFactory>()?.CreateLogger<IosCatalogEntry>()));
            services.AddSingleton<IDeviceProviderCatalogEntry>(sp => sp.GetRequiredService<IosCatalogEntry>());
            services.AddSingleton<IVmBackendSource>(sp => sp.GetRequiredService<IosCatalogEntry>());
        }
    }
}

/// <summary>
/// The catalogue entry and backend source of the iOS provider (doc 62 sections 9.1 and 9.2). The backend is built as soon as the
/// configuration is valid, whether or not a Mac node is linked; the node's state decides <c>Usable</c> on every probe, so a node that
/// connects later makes the provider usable without a restart. <c>vms.ios.max_concurrent</c> is live. Never throws.
/// </summary>
public sealed class IosCatalogEntry : DeviceProviderEntry<IosBackend>
{
    private readonly ISettingStore _settings;
    private readonly IConfiguration _configuration;
    private readonly IMacNodeLink _link;
    private readonly Func<IosOptions, IMacNodeLink, IosBackend> _factory;

    public IosCatalogEntry(
        ISettingStore settings,
        IConfiguration configuration,
        IMacNodeLink link,
        Func<IosOptions, IMacNodeLink, IosBackend>? factory = null,
        TimeProvider? clock = null,
        TimeSpan? refreshInterval = null,
        ILogger? logger = null)
        : base(clock, refreshInterval, logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _link = link ?? throw new ArgumentNullException(nameof(link));
        _factory = factory ?? ((options, l) => new IosBackend(l, options));
    }

    public override string Kind => IosModule.Kind;

    public override IReadOnlyList<VmOs> GuestOs { get; } = [VmOs.Ios];

    protected override DeviceProviderCapabilities DefaultCapabilities => IosModule.Capabilities;

    protected override string ReadFailureReason => "The iOS settings could not be read.";

    /// <summary>The golden image id that stands for the pinned runtime: configured, or derived from the runtime id and build.</summary>
    public static Guid RuntimeVersion(IConfiguration configuration, string runtimeId, string runtimeBuild)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (Guid.TryParse(configuration.GetSection(IosComponent.SectionName)["RuntimeVersion"], out var configured))
        {
            return configured;
        }

        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"bazaar:ios:{runtimeId}:{runtimeBuild}")).AsSpan(0, 16));
    }

    protected override async Task<(IosBackend? Backend, string? Reason)> BuildAsync(CancellationToken ct)
    {
        var max = await _settings.GetAsync(DeviceProvidersSettings.IosMaxConcurrent, cancellationToken: ct).ConfigureAwait(false);
        var section = _configuration.GetSection(IosComponent.SectionName);
        var runtimeId = section["RuntimeId"] ?? string.Empty;
        var runtimeBuild = section["RuntimeBuild"] ?? string.Empty;
        try
        {
            var options = new IosOptions
            {
                RuntimeId = runtimeId,
                RuntimeBuild = runtimeBuild,
                RuntimeVersion = RuntimeVersion(_configuration, runtimeId, runtimeBuild),
                DeviceType = section["DeviceType"] is { Length: > 0 } deviceType ? deviceType : IosDeviceProfile.DefaultDeviceType,
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

    protected override async Task<bool> ApplyLiveAsync(IosBackend backend, CancellationToken ct)
    {
        var max = await _settings.GetAsync(DeviceProvidersSettings.IosMaxConcurrent, cancellationToken: ct).ConfigureAwait(false);
        if (max == backend.MaxConcurrentInstances)
        {
            return false;
        }

        backend.SetMaxConcurrent(max);
        return true;
    }
}
