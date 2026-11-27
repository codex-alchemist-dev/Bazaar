using Bazaar.Server.ReviewLedger.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.ReviewLedger;

/// <summary>Composition of the review capture module.</summary>
public static class ReviewLedgerRegistration
{
    /// <summary>Registers review capture. The store is Postgres when an <c>NpgsqlDataSource</c> is registered (common/0013), otherwise in-memory.</summary>
    public static IServiceCollection AddBazaarReviewLedger(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new ReviewCaptureSettings());
        services.TryAddSingleton<IReviewStore>(sp =>
            sp.GetService<NpgsqlDataSource>() is { } dataSource ? new PgReviewStore(dataSource) : new InMemoryReviewStore());
        services.TryAddSingleton<IReviewRecorder, ReviewRecorder>();
        return services;
    }
}
