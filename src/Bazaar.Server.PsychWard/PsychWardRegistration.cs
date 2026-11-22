using Bazaar.LoopGuard.Abstractions;
using Bazaar.Server.PsychWard.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.PsychWard;

/// <summary>Composition of the Psych Ward module.</summary>
public static class PsychWardRegistration
{
    /// <summary>
    /// Registers the ward, vents and the loop-guard hand-off. Call before the loop-guard module's registration so this
    /// <see cref="IPsychWardGate"/> wins over its no-op default. Settings default to doc 26's shipped values; the host may
    /// register its own <see cref="PsychWardSettings"/> first. The host must register an <c>ITextSanitizer</c>. Stores are
    /// in-memory until the Postgres stores land.
    /// </summary>
    public static IServiceCollection AddBazaarPsychWard(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new PsychWardSettings());
        services.TryAddSingleton<IWardStayStore, InMemoryWardStayStore>();
        services.TryAddSingleton<IVentStore, InMemoryVentStore>();
        services.TryAddSingleton<IPsychWardService, PsychWardService>();
        services.TryAddSingleton<IVentService, VentService>();
        services.TryAddSingleton<IPsychWardGate, PsychWardGate>();
        return services;
    }
}
