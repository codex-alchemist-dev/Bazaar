using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.CodingReview;

/// <summary>
/// The coding review module's composition. It registers the pure context builder, the review service and the shipped size caps.
/// The ports (sources, model directory, reviewer session, result sink) are the server's to register. Each is a required service.
/// </summary>
public static class CodingReviewModule
{
    /// <summary>Registers the review module. Expects the server's implementations of the four ports to be registered already.</summary>
    public static IServiceCollection AddBazaarCodingReview(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(ReviewContextLimits.Default);
        services.TryAddSingleton<ReviewContextBuilder>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ReviewService>();
        return services;
    }
}
