using Bazaar.Server.Council.Membership;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Council.Hiring;

/// <summary>Dependency injection registration for the Talent hire motion adapter.</summary>
public static class HiringServiceRegistration
{
    /// <summary>
    /// Registers the classifier, the draft builder, <see cref="IHireGate"/> and the hire outcome sink. The host must also
    /// register <see cref="Service.ICouncilService"/> (the Council service) and <see cref="IHireDecisionStore"/>.
    /// <see cref="TimeProvider"/> defaults to the system clock unless the host registered one.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="biggerChangeClasses">The <c>council.bigger_change_classes</c> value. Null uses the shipped default.</param>
    /// <exception cref="ArgumentException">A configured class key is unknown. Thrown at registration, so a bad setting fails startup.</exception>
    public static IServiceCollection AddBazaarCouncilHiring(this IServiceCollection services, IEnumerable<string>? biggerChangeClasses = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new HireMotionClassifier(biggerChangeClasses));
        services.TryAddSingleton<HireMotionDraftBuilder>();
        services.TryAddSingleton<IHireGate, CouncilTalentHireGate>();
        services.AddSingleton<IMembershipOutcomeSink, HireOutcomeSink>();
        return services;
    }
}
