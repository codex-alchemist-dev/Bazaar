using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Biometric;

public static class BiometricRegistration
{
    /// <summary>
    /// Registers the assertion verifier. The host registers an <see cref="IBiometricCredentialStore"/> first (the persistent store
    /// arrives with enrolment, doc 32 question 4) and passes the policy.
    /// </summary>
    public static IServiceCollection AddBazaarBiometric(this IServiceCollection services, BiometricPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        services.TryAddSingleton(policy);
        services.TryAddSingleton<IBiometricAssertionVerifier, BiometricAssertionVerifier>();
        return services;
    }
}
