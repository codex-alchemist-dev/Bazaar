using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Council.Escalation;

/// <summary>Dependency injection registration for the owner escalation surface.</summary>
public static class CouncilEscalationRegistration
{
    /// <summary>
    /// Registers <see cref="CouncilEscalationService"/> and the re-notify worker. The host must have called
    /// <c>AddBazaarCouncilService</c> and registered <see cref="ICouncilEscalationSource"/> (the store does). It may register
    /// <see cref="ICouncilOwnerNotifier"/> and <see cref="ICouncilEscalationRenotifySource"/>; without a notifier the worker idles.
    /// </summary>
    public static IServiceCollection AddBazaarCouncilEscalation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new CouncilEscalationOptions());
        services.TryAddSingleton<CouncilEscalationService>();
        services.TryAddSingleton<CouncilEscalationRenotifier>();
        services.AddHostedService<CouncilEscalationRenotifyWorker>();
        return services;
    }
}
