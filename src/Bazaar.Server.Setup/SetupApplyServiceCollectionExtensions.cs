using Bazaar.Server.Security.Audit;
using Bazaar.Server.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Setup;

public static class SetupApplyServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SetupApplier"/> with the step list it is given. This slice registers no real steps: the database and
    /// custody steps arrive in later slices. The lock, audit log and settings audit sink must be registered by the caller.
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
            provider.GetRequiredService<ISettingAuditSink>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            // The composed registry carries module contributions (module manager keys); Known alone would reject them.
            provider.GetService<SettingRegistry>()));
        return services;
    }
}
