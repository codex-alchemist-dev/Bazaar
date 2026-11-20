using System.Collections.Concurrent;
using Bazaar.Server.Vms.Environments;

namespace Bazaar.Server.Vms.Lifecycle;

/// <summary>
/// The result of routing a VM: the provider kind and its backend, or the reason no backend may be used. <see cref="Kind"/> is null
/// only for a single-backend router, which records no kind.
/// </summary>
public sealed record VmRoute(string? Kind, IVmBackend? Backend, string? Reason)
{
    public bool Found => Backend is not null;

    public static VmRoute Refused(string reason) => new(null, null, reason);
}

/// <summary>Finds the backend that owns a VM (doc 62 section 9.2). The lifecycle service is its only caller.</summary>
public interface IVmBackendRouter
{
    /// <summary>Chooses the backend for a new VM. A refusal carries the reason; there is no fallback to another provider.</summary>
    ValueTask<VmRoute> RouteNewAsync(VmOs os, string? requestedKind, CancellationToken ct);

    /// <summary>The backend that owns <paramref name="vm"/>: its pinned backend, or the source named by its provider kind.</summary>
    ValueTask<VmRoute> RouteExistingAsync(EnvironmentVmRecord vm, CancellationToken ct);

    /// <summary>Keeps <paramref name="vmId"/> on <paramref name="backend"/> until <see cref="Release"/>.</summary>
    void Pin(Guid vmId, string? kind, IVmBackend backend);

    /// <summary>Drops a retired VM's pin.</summary>
    void Release(Guid vmId);
}

/// <summary>Routes every VM to one backend and records no kind: the doc 16 single-backend lifecycle.</summary>
public sealed class SingleBackendRouter(IVmBackend backend) : IVmBackendRouter
{
    private readonly IVmBackend _backend = backend ?? throw new ArgumentNullException(nameof(backend));

    public ValueTask<VmRoute> RouteNewAsync(VmOs os, string? requestedKind, CancellationToken ct) =>
        ValueTask.FromResult(new VmRoute(requestedKind, _backend, null));

    public ValueTask<VmRoute> RouteExistingAsync(EnvironmentVmRecord vm, CancellationToken ct) =>
        ValueTask.FromResult(new VmRoute(vm?.ProviderKind, _backend, null));

    public void Pin(Guid vmId, string? kind, IVmBackend backend)
    {
    }

    public void Release(Guid vmId)
    {
    }
}

/// <summary>
/// The multi-provider router (doc 62 section 9.2). Sources come from DI. A new VM goes to the kind it names, or to the selected pair
/// provider for Windows and Linux, or to the first enabled on-demand provider for any other OS. An existing VM goes to the source its
/// row names. Each VM stays pinned to the backend object it was created on until it is retired.
/// </summary>
public sealed class VmBackendRouter : IVmBackendRouter
{
    private readonly IReadOnlyList<IVmBackendSource> _ordered;
    private readonly Dictionary<string, IVmBackendSource> _byKind;
    private readonly VmPairSelection _pair;
    private readonly IVmProviderGate? _gate;
    private readonly ConcurrentDictionary<Guid, Pinned> _pins = new();

    /// <param name="sources">The registered providers. Two with the same kind are a registration error.</param>
    /// <param name="pair">The pair provider chosen at startup.</param>
    /// <param name="gate">Which on-demand kinds are enabled. Null means none is.</param>
    public VmBackendRouter(IEnumerable<IVmBackendSource> sources, VmPairSelection pair, IVmProviderGate? gate)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _pair = pair ?? throw new ArgumentNullException(nameof(pair));
        _gate = gate;
        _ordered = VmBackendOrdering.Order(sources);
        _byKind = new Dictionary<string, IVmBackendSource>(StringComparer.Ordinal);
        foreach (var source in _ordered)
        {
            if (!_byKind.TryAdd(source.Kind, source))
            {
                throw new ArgumentException($"Two VM providers register the kind '{source.Kind}'.", nameof(sources));
            }
        }
    }

    /// <summary>The registered sources in selection order.</summary>
    public IReadOnlyList<IVmBackendSource> Sources => _ordered;

    public async ValueTask<VmRoute> RouteNewAsync(VmOs os, string? requestedKind, CancellationToken ct)
    {
        var osName = EnvironmentText.ToDb(os);
        IVmBackendSource source;
        if (requestedKind is not null)
        {
            if (!_byKind.TryGetValue(requestedKind, out var named))
                return VmRoute.Refused($"No VM provider of kind '{requestedKind}' is installed on this host.");
            if (!named.GuestOs.Contains(os))
                return VmRoute.Refused($"The VM provider '{requestedKind}' does not run {osName} guests.");
            if (named.Placement == VmProviderPlacement.OnDemand && !await IsEnabledAsync(named.Kind, ct).ConfigureAwait(false))
                return VmRoute.Refused($"The VM provider '{requestedKind}' is not enabled in vms.devices.enabled.");
            source = named;
        }
        else if (os is VmOs.Windows or VmOs.Linux)
        {
            if (PairSource(os) is not { } pair)
                return VmRoute.Refused($"No VM provider is selected for {osName} guests (host.provider.vm), and the request names none.");
            source = pair;
        }
        else
        {
            IVmBackendSource? first = null;
            foreach (var candidate in _ordered)
            {
                if (candidate.Placement == VmProviderPlacement.OnDemand && candidate.GuestOs.Contains(os)
                    && await IsEnabledAsync(candidate.Kind, ct).ConfigureAwait(false))
                {
                    first = candidate;
                    break;
                }
            }

            if (first is null)
                return VmRoute.Refused($"No enabled VM provider runs {osName} guests (vms.devices.enabled).");
            source = first;
        }

        // The first matching provider is the answer. If it cannot serve now the create is refused; the next one is never tried.
        var lookup = await source.GetCurrentAsync(ct).ConfigureAwait(false);
        if (lookup.Backend is null || !lookup.Usable)
            return VmRoute.Refused($"The VM provider '{source.Kind}' cannot create a VM now: {lookup.Reason ?? "not usable on this host"}.");
        return new VmRoute(source.Kind, lookup.Backend, null);
    }

    public async ValueTask<VmRoute> RouteExistingAsync(EnvironmentVmRecord vm, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vm);
        if (_pins.TryGetValue(vm.Id, out var pinned))
            return new VmRoute(pinned.Kind, pinned.Backend, null);

        IVmBackendSource? source;
        if (vm.ProviderKind is { } kind)
        {
            if (!_byKind.TryGetValue(kind, out source))
                return VmRoute.Refused($"The VM's provider '{kind}' is not installed on this host.");
        }
        else
        {
            source = LegacySource(vm.Os);
            if (source is null)
                return VmRoute.Refused($"The VM records no provider, and no single provider runs {EnvironmentText.ToDb(vm.Os)} guests here.");
        }

        // The enabled list is not checked here, so a disabled provider can still stop and retire the guests it owns.
        var lookup = await source.GetCurrentAsync(ct).ConfigureAwait(false);
        if (lookup.Backend is null)
            return VmRoute.Refused($"The VM provider '{source.Kind}' has no backend: {lookup.Reason ?? "not configured"}.");

        var pin = _pins.GetOrAdd(vm.Id, new Pinned(source.Kind, lookup.Backend));
        return new VmRoute(pin.Kind, pin.Backend, null);
    }

    public void Pin(Guid vmId, string? kind, IVmBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _pins[vmId] = new Pinned(kind, backend);
    }

    public void Release(Guid vmId) => _pins.TryRemove(vmId, out _);

    private IVmBackendSource? PairSource(VmOs os) =>
        _pair.Kind is { } kind && _byKind.TryGetValue(kind, out var source) && source.GuestOs.Contains(os) ? source : null;

    // A row from before routing: the pair provider if it runs the OS, else the one registered provider that does.
    private IVmBackendSource? LegacySource(VmOs os)
    {
        if (PairSource(os) is { } pair)
            return pair;
        var covering = _ordered.Where(s => s.GuestOs.Contains(os)).Take(2).ToList();
        return covering.Count == 1 ? covering[0] : null;
    }

    private async ValueTask<bool> IsEnabledAsync(string kind, CancellationToken ct) =>
        _gate is not null && await _gate.IsEnabledAsync(kind, ct).ConfigureAwait(false);

    private sealed record Pinned(string? Kind, IVmBackend Backend);
}
