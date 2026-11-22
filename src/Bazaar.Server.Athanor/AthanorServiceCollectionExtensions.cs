using Bazaar.Server.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Athanor;

/// <summary>Registration of the Athanor server module (doc 57 section 6).</summary>
public static class AthanorServiceCollectionExtensions
{
    /// <summary>
    /// Adds the open and save handlers, the rate limiter, default limits, the on-disk store with its resolvers (configured
    /// workspaces, and git clones when an <c>IGitRepositoryDirectory</c> is registered), the role-based authorizer and the settings
    /// contribution. The host must register <c>IAuditLog</c> and <c>IPermissionChecker</c>, and adapt the handlers to its wire
    /// dispatcher. Every registration is TryAdd, so a host or test may replace one (for example <see cref="IAthanorLimits"/> backed by settings).
    /// </summary>
    public static IServiceCollection AddBazaarAthanor(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAthanorLimits, AthanorLimits>();
        services.TryAddSingleton<SaveRateLimiter>();
        services.TryAddSingleton(new AthanorWorkspaceOptions());
        services.TryAddSingleton<IEditorWorkspaceResolver>(sp =>
        {
            var options = sp.GetRequiredService<AthanorWorkspaceOptions>();
            var resolvers = new List<IEditorWorkspaceResolver> { new ConfiguredEditorWorkspaceResolver(options) };
            var git = sp.GetService<Bazaar.Server.Git.IGitRepositoryDirectory>();
            if (git is not null)
            {
                resolvers.Add(new GitRepositoryEditorWorkspaceResolver(git, options));
            }

            return new CompositeEditorWorkspaceResolver(resolvers);
        });
        services.TryAddSingleton<IEditorWorkspaceStore, DirectoryEditorWorkspaceStore>();
        services.TryAddSingleton<IEditorAuthorizer, RolesEditorAuthorizer>();
        services.TryAddSingleton<EditorOpenHandler>();
        services.TryAddSingleton<EditorSaveHandler>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISettingContribution, AthanorSettingContribution>());
        return services;
    }
}
