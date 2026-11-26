using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.CodingEdit;

/// <summary>
/// The coding edit module's composition: the three edit tools, the router and the metrics. The host supplies the
/// <see cref="IWorktreeFiles"/> for each agent's worktree, because no default store is safe to register.
/// </summary>
public static class CodingEditModule
{
    /// <summary>Registers the edit tools, the router and the metrics. Expects <see cref="IWorktreeFiles"/> to be registered already.</summary>
    public static IServiceCollection AddBazaarCodingEdit(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEditTool, WholeFileEditTool>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEditTool, LineAnchoredEditTool>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEditTool, SearchReplaceEditTool>());
        services.TryAddSingleton<EditMetrics>();
        services.TryAddSingleton<EditToolRouter>();
        return services;
    }
}
