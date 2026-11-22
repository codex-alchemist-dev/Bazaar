using System.Text.Json;
using Bazaar.Core;
using Bazaar.Server.AgentSafety.Containment;
using Bazaar.Server.Settings;
using Bazaar.Server.VmEgress;
using Bazaar.Server.VmEgress.Approvals;
using Bazaar.Server.VmEgress.Approvals.Security;
using Bazaar.Server.VmMounts;
using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.VmMounts.Providers;
using Bazaar.Server.Vms.Environments;
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
/// <item><description><b>Taint</b>: the egress taint reader uses Security's <c>IAgentSessionTaintReader</c>, which the agent-turns component registers; if
/// that reader is absent the gate denies growth requests.</description></item>
/// <item><description><b>Scoped mounts</b>: the three host ports now have implementations in <c>Bazaar.Server.VmMounts.Providers</c>
/// (<see cref="MountProvidersRegistration.AddBazaarVmMountProviders"/>): an SMB provider on a Windows host, a directory provider elsewhere, a
/// guest channel adapter and a canary sink that audits and contains. They are registered, and the mount module composed, only when the
/// <c>vm-mounts</c> component is not off and <c>VmMounts:AgentsRoot</c> is set; otherwise <see cref="IMountManager"/> is absent and the kill
/// switch's mount leg fails loudly. Gaps left in force (all fail closed): no <c>IVmShareIdentity</c> (per-VM share account) and no
/// <c>IVmShareEndpoint</c> (virtual switch address), so an SMB attach refuses; the guest channel provider now reads the transport's channel
/// directory (<see cref="IGuestChannelDirectory"/>), but nothing registers a channel yet (see the channel directory gap below), so every guest
/// file operation still reports no channel; the guest protocol has no recursive listing or directory delete, so a Soul
/// attach rolls back at the profile copy and only workspace mounts complete; SMB canary reads cannot be observed; the guest-side mount is unbuilt.</description></item>
/// <item><description><b>Channel directory</b>: the transport module registers the registry, the directory and <c>GuestChannelRegistrar</c>; the host
/// supplies <see cref="IAgentVmLookup"/> over the environment repository. Remaining gap: no code calls <c>GuestChannelFactory.OpenAsync</c> after a VM
/// starts and then <c>GuestChannelRegistrar.RegisterOpenedAsync</c> (who dials, the nonce, and which listener per OS), so the directory is empty.
/// The egress target resolver is built from the same lookup inside <c>AddBazaarVmEgressEnforcers</c>, which this host does not call yet; the
/// lookup has no guest interface, so the nftables enforcer would refuse.</description></item>
/// <item><description><b>Guest scrubber</b>: the real <c>VmGuestScrubber</c> needs a VM lifecycle service, which exists only when a VM backend is configured
/// (<c>Bazaar:Vms:WindowsSandbox</c>). Without one the scrubber stays unavailable and refuses.</description></item>
/// <item><description><b>Approvals</b>: the egress port uses the approvals service from agent safety; the owner-approval verifier is the unbuilt
/// part.</description></item>
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

        // The three ports first, so the guard below passes when the module is enabled and has a root. TryAdd: a host override registered
        // earlier wins. Without a root or with the component off, nothing is registered and the manager stays absent.
        if (MountsConfigured(configuration))
        {
            services.AddBazaarVmMountProviders();
        }

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

        // The VM store port for the channel directory and the egress resolver. TryAdd before the transport module's fail-closed default, so a
        // host override or a test registration wins. Without the Vms module's repository there are no VMs to serve.
        services.TryAddSingleton<IAgentVmLookup>(sp => sp.GetService<IAgentVmDirectory>() is { } directory
            ? new RepositoryAgentVmLookup(directory)
            : new NoAgentVmLookup());
        services.AddBazaarVmTransport();
        return services;
    }

    /// <summary>The mount module is wanted: its component is not off and it has a root.</summary>
    private static bool MountsConfigured(IConfiguration configuration) =>
        BazaarComponentRegistration.IsEnabled(configuration, VmMountsComponent.ManifestDefinition.Id)
        && !string.IsNullOrWhiteSpace(configuration.GetSection("VmMounts")["AgentsRoot"]);

    /// <summary>The mount module needs to be wanted and the three host-side ports to be registered (by the providers module or a host override).</summary>
    private static bool MountsCanBeComposed(IServiceCollection services, IConfiguration configuration) =>
        MountsConfigured(configuration)
        && services.Any(d => d.ServiceType == typeof(IMountProvider))
        && services.Any(d => d.ServiceType == typeof(IMountGuestChannel))
        && services.Any(d => d.ServiceType == typeof(ICanarySink));
}

/// <summary>
/// <see cref="IAgentVmLookup"/> over the Vms module's <see cref="IAgentVmDirectory"/> (the environment repository). A VM is retired when its
/// state is <see cref="VmState.Retired"/>; the VM name is the backend ref. The record carries no guest interface, so none is reported.
/// </summary>
internal sealed class RepositoryAgentVmLookup(IAgentVmDirectory directory) : IAgentVmLookup
{
    public async ValueTask<IReadOnlyList<AgentVmInfo>> ListAsync(Guid agentId, CancellationToken ct)
    {
        var records = await directory.ListAsync(agentId, ct).ConfigureAwait(false);
        return [.. records.Select(r => new AgentVmInfo(
            r.Id,
            r.Os == VmOs.Windows ? GuestOs.Windows : GuestOs.Linux,
            r.BackendRef,
            r.State == VmState.Retired))];
    }
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
