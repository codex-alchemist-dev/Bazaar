using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Gc.Provenance;

public static class GcProvenanceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the default trust tagging hook (<see cref="DefaultGcTrustHook"/>) that the GC writer calls to derive
    /// <c>tainted_at_write</c> and <c>taint_sources</c>. Called from <see cref="GcLogServiceCollectionExtensions.AddGcLog"/>.
    /// </summary>
    // TODO(component): make the member-human taint setting (doc 15 says "configurable") an operator setting once the
    // settings registry (doc 03) exists. Until then member-human content is always clean.
    public static IServiceCollection AddBazaarGcProvenance(this IServiceCollection services)
    {
        services.TryAddSingleton<IGcTrustHook, DefaultGcTrustHook>();
        return services;
    }
}
