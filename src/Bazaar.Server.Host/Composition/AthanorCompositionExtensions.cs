using Bazaar.Server.Athanor;
using Bazaar.Server.Athanor.Vault;
using Bazaar.Server.Vault.Git;
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
        AddVaultWorkspace(services, configuration);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWireRequestHandler, EditorOpenWireHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWireRequestHandler, EditorSaveWireHandler>());
        return services;
    }

    /// <summary>
    /// When <c>Memory:Vault:RepoPath</c> is set, the <c>vault</c> workspace is that checkout and its saves go through the Vault writer
    /// (doc 57 section 6, doc 18 write path). Commits are authored as <c>Memory:Vault:WriterName</c> and <c>WriterEmail</c>; edit worktrees
    /// live in <c>Bazaar:Athanor:VaultWorktreeRoot</c> (default: a sibling folder <c>&lt;RepoPath&gt;.athanor-worktrees</c>).
    /// </summary>
    private static void AddVaultWorkspace(IServiceCollection services, IConfiguration configuration)
    {
        var repoPath = configuration["Memory:Vault:RepoPath"];
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            return;
        }

        var name = configuration["Memory:Vault:WriterName"];
        var email = configuration["Memory:Vault:WriterEmail"];
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email))
        {
            throw new InvalidOperationException("Memory:Vault:WriterName and Memory:Vault:WriterEmail are required when Memory:Vault:RepoPath is set.");
        }

        var worktreeRoot = configuration[ConfigurationSection + ":VaultWorktreeRoot"] is { Length: > 0 } configured
            ? configured
            : repoPath.TrimEnd('/', '\\') + ".athanor-worktrees";
        services.AddBazaarAthanorVault(new VaultEditorOptions(VaultEditorOptions.DefaultWorkspaceId, repoPath, worktreeRoot, new GitIdentity(name, email)));
    }
}
