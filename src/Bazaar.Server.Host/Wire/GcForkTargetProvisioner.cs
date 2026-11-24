using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Create;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.TimeMachine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Host.Wire;

/// <summary>
/// Binds the Time Machine's fork seam to the GC module (docs/design/26, section 4, fork target decisions). It creates one empty
/// project GC through <see cref="IGcProvisioner"/> with the requester as the only member, no parent and no link metadata; the
/// <c>fork_link</c> row owned by the Time Machine records the link. The GC id is derived from the request id, so a retry
/// finds the same GC. The Vault branch is the Vault module's job and is not made here, so <see cref="ForkTarget.VaultCommit"/>
/// is null until a Vault adapter decorates this one.
/// </summary>
public sealed class GcForkTargetProvisioner(IGcProvisioner gcs) : IForkTargetProvisioner
{
    private const string _seedPrefix = "time-machine.fork:";

    /// <summary>The GC id a fork request lands in. Stable for a request id.</summary>
    public static Guid TargetId(Guid requestId) => GcCreateService.DeterministicGcId(_seedPrefix + requestId.ToString("N"));

    /// <inheritdoc />
    public async Task<ForkTarget> ProvisionAsync(Guid requestId, Guid sourceGcId, long effectiveSeq, GcPrincipal requester, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requester);

        var id = TargetId(requestId);
        var title = $"Fork of {sourceGcId.ToString("N")[..8]} at seq {effectiveSeq}";
        await gcs.CreateProjectGcAsync(id, title, requester, ct).ConfigureAwait(false);
        return new ForkTarget(id, null);
    }
}

/// <summary>Registers the GC-backed fork target provisioner.</summary>
public static class GcForkTargetServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="GcForkTargetProvisioner"/> as the fork target provisioner when the GC provisioner is already registered,
    /// so forks work. Call it after the components are added. Without a GC provisioner nothing is added and forks answer <c>unsupported</c>. A host that binds its own
    /// <see cref="IForkTargetProvisioner"/> first keeps it.
    /// </summary>
    public static IServiceCollection AddBazaarGcForkTarget(this IServiceCollection services)
    {
        // The GC module implements the seam on GcCreateService; a host that registers only that class still gets forks.
        if (services.Any(d => d.ServiceType == typeof(IGcProvisioner)))
        {
            services.TryAddSingleton<IForkTargetProvisioner>(sp => new GcForkTargetProvisioner(sp.GetRequiredService<IGcProvisioner>()));
        }
        else if (services.Any(d => d.ServiceType == typeof(GcCreateService)))
        {
            services.TryAddSingleton<IForkTargetProvisioner>(sp => new GcForkTargetProvisioner(sp.GetRequiredService<GcCreateService>()));
        }

        return services;
    }
}
