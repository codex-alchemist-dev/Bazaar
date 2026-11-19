using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.AgentSafety.AutoContainment;

/// <summary>Registration for automatic containment. Not part of the module's default wiring.</summary>
public static class AutoContainmentServiceCollectionExtensions
{
    /// <summary>
    /// Registers the auto-containment service, its undo, an in-memory record store and a null notice sink (all TryAdd, so durable
    /// or real ones registered first win), and validates <see cref="AutoContainmentOptions"/> on start. The caller registers
    /// <c>IAnomalyAlertSource</c>, <c>IAgentContainment</c>, <c>IContainmentStateStore</c>, <c>IAuditLog</c> and <c>TimeProvider</c>.
    /// </summary>
    public static IServiceCollection AddBazaarAutoContainment(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<AutoContainmentOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AutoContainmentOptions>, AutoContainmentOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IContainmentRecordStore, InMemoryContainmentRecordStore>();
        services.TryAddSingleton<INoticeSink, NullNoticeSink>();
        services.TryAddSingleton<IAutoContainmentUndo, AutoContainmentUndo>();
        if (!services.Any(d => d.ServiceType == typeof(AutoContainmentService)))
        {
            services.AddSingleton<AutoContainmentService>();
            services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<AutoContainmentService>());
        }
        return services;
    }
}
