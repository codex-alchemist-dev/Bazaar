using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Bazaar.Server.Jobs;

public static class JobsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the job queue and runs its poller as a hosted service. Components register their <see cref="IJobHandler"/>s and
    /// their <see cref="RecurringJob"/>s; the poller enqueues those on each poll. Needs an <c>NpgsqlDataSource</c> and
    /// <see cref="TimeProvider"/>, which the host already registers.
    /// </summary>
    public static IServiceCollection AddBazaarJobs(this IServiceCollection services)
    {
        services.TryAddSingleton(new JobPollerOptions());
        services.TryAddSingleton<JobStore>();
        services.TryAddSingleton<RecurringJobScheduler>();
        services.TryAddSingleton<JobPoller>();
        services.AddHostedService<JobPollerService>();
        return services;
    }
}
