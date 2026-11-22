using Bazaar.Server.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Athanor;

/// <summary>Registration of the Athanor server module (doc 57 section 6).</summary>
public static class AthanorServiceCollectionExtensions
{
    /// <summary>
    /// Adds the open and save handlers, the rate limiter, default limits and the settings contribution. The host must register
    /// <see cref="IEditorWorkspaceStore"/>, <see cref="IEditorAuthorizer"/> and <c>IAuditLog</c>, and adapt the handlers to its wire
    /// dispatcher. Every registration is TryAdd, so a host or test may replace one (for example <see cref="IAthanorLimits"/> backed by settings).
    /// </summary>
    public static IServiceCollection AddBazaarAthanor(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAthanorLimits, AthanorLimits>();
        services.TryAddSingleton<SaveRateLimiter>();
        services.TryAddSingleton<EditorOpenHandler>();
        services.TryAddSingleton<EditorSaveHandler>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISettingContribution, AthanorSettingContribution>());
        return services;
    }
}
