using Bazaar.Server.Vms.Environments;

namespace Bazaar.Server.Vms.Lifecycle;

/// <summary>Where a provider may serve (doc 62 section 9.2).</summary>
public enum VmProviderPlacement
{
    /// <summary>May serve doc 16's Windows and Linux pair, chosen by <c>host.provider.vm</c>.</summary>
    Pair,

    /// <summary>An extra guest an environment asks for. The kind must be listed in <c>vms.devices.enabled</c> before a new VM can use it.</summary>
    OnDemand,
}

/// <summary>
/// What a provider module contributes to the lifecycle (doc 62 section 9.2). Each provider registers one through DI; the host lists
/// none by name. A source that is also an <c>IVmProvider</c> can be a pair candidate for the provider selector.
/// </summary>
public interface IVmBackendSource
{
    /// <summary>The kind stored in <c>environment_vm.provider_kind</c>, for example <c>windows_sandbox</c> or <c>android</c>.</summary>
    string Kind { get; }

    /// <summary>The guest operating systems this provider creates.</summary>
    IReadOnlyList<VmOs> GuestOs { get; }

    VmProviderPlacement Placement { get; }

    /// <summary>Selection order: lower first, ties broken by kind (<see cref="VmBackendOrdering"/>).</summary>
    int Preference { get; }

    /// <summary>
    /// The backend for the next operation, whether it is usable now, and a plain reason when it is not. May re-read live settings.
    /// Never throws for a configuration problem; it reports it.
    /// </summary>
    ValueTask<VmBackendLookup> GetCurrentAsync(CancellationToken ct);
}

/// <summary>
/// The answer of <see cref="IVmBackendSource.GetCurrentAsync"/>. <paramref name="Backend"/> may be set while <paramref name="Usable"/> is
/// false (for example a failed host probe): an existing VM can still be stopped or retired on it, a new one is refused.
/// </summary>
public sealed record VmBackendLookup(IVmBackend? Backend, bool Usable, string? Reason)
{
    public static VmBackendLookup Ready(IVmBackend backend) => new(backend ?? throw new ArgumentNullException(nameof(backend)), true, null);

    public static VmBackendLookup Unavailable(string reason) => new(null, false, reason);
}

/// <summary>Answers whether an on-demand provider kind is switched on (<c>vms.devices.enabled</c>).</summary>
public interface IVmProviderGate
{
    ValueTask<bool> IsEnabledAsync(string kind, CancellationToken ct);
}

/// <summary>The pair provider kind the selector chose at startup, or null when the VM role is <c>none</c> or nothing was usable.</summary>
public sealed record VmPairSelection(string? Kind)
{
    public static VmPairSelection None { get; } = new((string?)null);
}

/// <summary>The documented order of provider sources (doc 62 section 9.2): preference ascending, then kind by ordinal.</summary>
public static class VmBackendOrdering
{
    /// <summary>Shipped preference of Hyper-V and KVM.</summary>
    public const int PrimaryHypervisor = 100;

    /// <summary>Shipped preference of on-demand device and microVM providers.</summary>
    public const int OnDemand = 500;

    /// <summary>Shipped preference of a last-resort provider such as Windows Sandbox.</summary>
    public const int LastResort = 900;

    public static IReadOnlyList<T> Order<T>(IEnumerable<T> sources)
        where T : IVmBackendSource
    {
        ArgumentNullException.ThrowIfNull(sources);
        return sources.OrderBy(s => s.Preference).ThenBy(s => s.Kind, StringComparer.Ordinal).ToList();
    }
}
