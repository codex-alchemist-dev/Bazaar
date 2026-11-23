using Bazaar.Server.Athanor;
using Bazaar.Server.Wire.Connection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Host.Composition;

/// <summary>Host wiring for the Athanor editor module (doc 57 section 6): limits from settings, workspaces from configuration, the two wire handlers.</summary>
public static class AthanorCompositionExtensions
{
    /// <summary>The configuration section the workspaces are bound from.</summary>
    public const string ConfigurationSection = "Bazaar:Athanor";

    /// <summary>
    /// Registers the Athanor module and its host adapter. Needs <c>ISettingStore</c>, <c>IAuditLog</c> and <c>IPermissionChecker</c>
    /// from the host's components. Workspaces come from <c>Bazaar:Athanor:Workspaces</c> (empty by default, so nothing is editable
    /// until the operator names a root); an <c>IGitRepositoryDirectory</c> adds <c>git:&lt;repositoryId&gt;</c> workspaces when one is registered.
    /// </summary>
    public static IServiceCollection AddBazaarHostAthanor(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration.GetSection(ConfigurationSection).Get<AthanorWorkspaceOptions>() ?? new AthanorWorkspaceOptions();
        services.TryAddSingleton(options);
        services.TryAddSingleton<SettingsAthanorLimits>();
        services.TryAddSingleton<IAthanorLimits>(sp => sp.GetRequiredService<SettingsAthanorLimits>());
        services.AddBazaarAthanor();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWireRequestHandler, EditorOpenWireHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWireRequestHandler, EditorSaveWireHandler>());
        return services;
    }
}
