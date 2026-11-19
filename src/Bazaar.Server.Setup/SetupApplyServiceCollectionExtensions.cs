using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Setup;

public static class SetupApplyServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SetupApplier"/> with the step list it is given. This slice registers no real steps: the database and
    /// custody steps arrive in later slices. The lock, audit log and settings audit and settings catalog must be registered by the caller.
    /// </summary>
    public static IServiceCollection AddSetupApply(this IServiceCollection services, IEnumerable<ISetupStep> steps)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(steps);

        var stepList = steps.ToList();
        services.AddSingleton<SetupApplier>(provider => new SetupApplier(
            provider.GetRequiredService<ISetupInstanceLock>(),
            stepList,
            provider.GetRequiredService<IAuditLog>(),
            provider.GetRequiredService<ISetupSettingAudit>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetRequiredService<ISetupSettingCatalog>()));
        return services;
    }
}
