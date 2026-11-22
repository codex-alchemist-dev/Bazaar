using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Setup;

public static class SetupApplyServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SetupApplier"/> with the step list it is given. The lock, audit log and settings audit and settings catalog must be registered by the caller.
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

    /// <summary>
    /// Registers <see cref="SetupApplier"/> with the steps registered in the container as <see cref="ISetupStep"/>, so a composition
    /// root adds a step by registering it. No registered step means an apply that changes anything ends in <c>no_step_implementation</c>.
    /// </summary>
    public static IServiceCollection AddSetupApply(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<SetupApplier>(provider => new SetupApplier(
            provider.GetRequiredService<ISetupInstanceLock>(),
            provider.GetServices<ISetupStep>(),
            provider.GetRequiredService<IAuditLog>(),
            provider.GetRequiredService<ISetupSettingAudit>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetRequiredService<ISetupSettingCatalog>()));
        return services;
    }
}
