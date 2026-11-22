using System.Text.Json;
using Bazaar.Core;
using Bazaar.Server.Settings;
using Bazaar.Server.VmEgress;
using Bazaar.Server.VmEgress.Approvals;
using Bazaar.Server.VmEgress.Approvals.Security;
using Bazaar.Server.VmMounts;
using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.VmTransport;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// The VM security modules in the host (docs/design/16-agent-vms-and-environments.md, "Host composition"): egress policy, the approval-backed
/// egress gate, scoped mounts and the guest transport. Each module is wired through its own public registration; nothing here re-implements one.
/// </summary>
/// <remarks>
/// <para>
/// Fail closed, by design. Where the repository has no real implementation yet, the module's own default stays in place and no stub is added
/// that could allow anything. Gaps left in force today:
/// </para>
/// <list type="bullet">
/// <item><description><b>Egress enforcer</b>: no Hyper-V or nftables executor exists, so <see cref="IEgressEnforcer"/> stays
/// <c>UnavailableEgressEnforcer</c>, which throws; the egress policy reports it instead of claiming default-deny.</description></item>
/// <item><description><b>Egress allowlist store</b>: only the in-memory store exists; allowlist growth is lost on restart. A durable store is unbuilt.</description></item>
/// <item><description><b>Egress allowlist settings</b>: <c>security.egress.default_allowlist</c> and <c>vm.egress.allowlist_change_approval</c> have no
/// setting contribution yet, so the options keep their shipped values (empty allowlist, approval tier floor).</description></item>
/// <item><description><b>Taint lookup</b>: no <see cref="IAgentSessionTaintLookup"/> exists (the agent to session map lives in Bazaar.Server, which modules may not
/// reference), so the egress taint reader cannot read a session and the gate denies growth requests.</description></item>
/// <item><description><b>Scoped mounts</b>: no <see cref="IMountProvider"/>, <see cref="IMountGuestChannel"/> or <see cref="ICanarySink"/> implementation
/// exists. The mount module is composed only when all three are already registered, <c>Bazaar:Components:vm-mounts</c> is not off and
/// <c>VmMounts:AgentsRoot</c> is set; otherwise <see cref="IMountManager"/> is absent and the kill switch's mount leg fails loudly. Registering the manager
/// without them would make it unresolvable and break containment resolution, so it is not done.</description></item>
/// <item><description><b>Guest scrubber</b>: the real <c>VmGuestScrubber</c> needs a VM lifecycle service, which exists only when a VM backend is configured
/// (<c>Bazaar:Vms:WindowsSandbox</c>). Without one the scrubber stays unavailable and refuses.</description></item>
/// <item><description><b>Approvals and taint</b>: the egress port uses the approvals service from agent safety; the owner-approval verifier and the
/// taint lookup above are the unbuilt parts.</description></item>
/// </list>
/// <para>
/// The guest transport listener is registered by <see cref="VmTransportRegistration.AddBazaarVmTransport"/> (vsock on Linux). On Windows the host
/// wraps the Hyper-V socket listener so it reads <c>vm.transport.service_guid</c> from the settings store on first use.
/// </para>
/// </remarks>
public static class VmSecurityCompositionExtensions
{
    /// <summary>Registers the VM security modules. Call after <c>AddBazaarVms</c> and before <c>AddBazaarAgentSafetyModule</c> (which replaces containment and scrubber over these).</summary>
    public static IServiceCollection AddBazaarVmSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddBazaarVmEgress();
        services.AddBazaarVmEgressApprovals();
        services.AddBazaarVmEgressApprovalsSecurity();

        if (MountsCanBeComposed(services, configuration))
        {
            services.AddBazaarVmMounts(configuration);
        }

        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISettingContribution, VmTransportSettingContribution>());
        if (OperatingSystem.IsWindows())
        {
            services.TryAddSingleton<IGuestConnectionListener>(sp => new SettingBackedHvSocketListener(
                sp.GetRequiredService<ISettingStore>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<SettingBackedHvSocketListener>(),
                options => new HvSocketListener(options)));
        }

        services.AddBazaarVmTransport();
        return services;
    }

    /// <summary>The mount module needs its component on, a root, and the three host-side ports that have no implementation yet.</summary>
    private static bool MountsCanBeComposed(IServiceCollection services, IConfiguration configuration) =>
        BazaarComponentRegistration.IsEnabled(configuration, VmMountsComponent.ManifestDefinition.Id)
        && !string.IsNullOrWhiteSpace(configuration.GetSection("VmMounts")["AgentsRoot"])
        && services.Any(d => d.ServiceType == typeof(IMountProvider))
        && services.Any(d => d.ServiceType == typeof(IMountGuestChannel))
        && services.Any(d => d.ServiceType == typeof(ICanarySink));
}

/// <summary>
/// The Hyper-V socket listener with its service GUID read from <c>vm.transport.service_guid</c> on first wait, because listeners are built
/// synchronously and the setting store is asynchronous. A stored value that is not a valid non-empty GUID falls back to the default and is
/// logged. A store outage is not cached as the default: the wait throws and the next wait reads again.
/// </summary>
internal sealed partial class SettingBackedHvSocketListener(
    ISettingStore settings,
    ILogger<SettingBackedHvSocketListener> logger,
    Func<HvSocketListenerOptions, IGuestConnectionListener> create) : IGuestConnectionListener
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IGuestConnectionListener? _inner;

    public GuestTransportKind Kind => GuestTransportKind.HvSocket;

    public async ValueTask<GuestConnection> AcceptAsync(string vmId, CancellationToken ct)
    {
        var inner = await EnsureAsync(ct).ConfigureAwait(false);
        return await inner.AcceptAsync(vmId, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_inner is not null)
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }

    private async Task<IGuestConnectionListener> EnsureAsync(CancellationToken ct)
    {
        if (_inner is { } ready)
        {
            return ready;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _inner ??= create(await ReadOptionsAsync(ct).ConfigureAwait(false));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HvSocketListenerOptions> ReadOptionsAsync(CancellationToken ct)
    {
        JsonElement? stored;
        string? problem;
        try
        {
            var value = await settings.GetAsync(VmTransportSettings.ServiceGuid, cancellationToken: ct).ConfigureAwait(false);
            stored = JsonSerializer.SerializeToElement(value);
        }
        catch (InvalidSettingValueException ex)
        {
            LogInvalid(logger, ex.Message);
            return HvSocketListenerOptions.Default;
        }

        var options = HvSocketListenerOptions.FromSettings(stored, out problem);
        if (problem is not null)
        {
            LogInvalid(logger, problem);
        }

        return options;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "vm.transport.service_guid is not usable: {Problem}")]
    private static partial void LogInvalid(ILogger logger, string problem);
}
