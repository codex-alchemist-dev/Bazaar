using Bazaar.Roles.Abstractions;
using Bazaar.Server.Biometric;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Roles.Biometric;

public static class RolesBiometricRegistration
{
    /// <summary>
    /// Registers the biometric widening check. Once it is registered every widening also needs a valid assertion, so it needs an
    /// <see cref="IBiometricAssertionVerifier"/> in the container; resolving the gate without one fails at start-up, not at first use.
    /// </summary>
    public static IServiceCollection AddBazaarRolesBiometric(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IWideningBiometricCheck>(sp =>
            new BiometricWideningCheck(sp.GetRequiredService<IBiometricAssertionVerifier>(), sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}
