using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.DeviceEnrolment;

public static class DeviceEnrolmentRegistration
{
    /// <summary>
    /// Registers the ceremony. The host supplies <see cref="IEnrolmentStore"/>, <see cref="IEnrolmentAudit"/>, one attestor per platform
    /// and the owner-signature and biometric verifiers; the module registers none of them, so a missing piece fails at start, not
    /// as a weaker check.
    /// Registers the ceremony. The host supplies <see cref="IEnrolmentStore"/>, <see cref="IEnrolmentAudit"/> and the owner-signature and
    /// biometric verifiers; the module registers none of them, so a missing piece fails at start, not as a weaker check. The two
    /// attestors default to <see cref="RefusingApprovalKeyAttestor"/> and <see cref="RefusingCredentialAttestor"/>, which refuse every
    /// enrolment: until a host registers a real verifier first (before this call), attestation that cannot be verified is refused.
    /// </summary>
    public static IServiceCollection AddBazaarDeviceEnrolment(this IServiceCollection services, EnrolmentOptions? options = null)
    {
        services.TryAddSingleton(options ?? new EnrolmentOptions());
        services.TryAddSingleton<IApprovalKeyAttestor, RefusingApprovalKeyAttestor>();
        services.TryAddSingleton<ICredentialAttestor, RefusingCredentialAttestor>();
        services.TryAddSingleton<IDeviceEnrolment, DeviceEnrolment>();
        return services;
    }
}
