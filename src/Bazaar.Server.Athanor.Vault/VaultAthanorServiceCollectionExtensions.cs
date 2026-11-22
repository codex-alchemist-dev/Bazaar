using Bazaar.Server.Athanor;
using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Writes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Athanor.Vault;

/// <summary>Registers the Vault workspace of the editor (doc 57 section 6, PROPOSED).</summary>
public static class VaultAthanorServiceCollectionExtensions
{
    /// <summary>
    /// Makes <paramref name="options"/>'s workspace id resolve to the Vault checkout and routes its saves through <see cref="IVaultWriter"/>.
    /// Call after <c>AddBazaarAthanor</c>; needs <see cref="IGitProvider"/> and <see cref="IVaultWriter"/> (the Memory component registers both
    /// when <c>Memory:Vault:RepoPath</c> is set). The Vault root always comes from <paramref name="options"/>; an operator entry with the same id can only make it read-only.
    /// </summary>
    public static IServiceCollection AddBazaarAthanorVault(this IServiceCollection services, VaultEditorOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        var existingResolver = services.LastOrDefault(d => d.ServiceType == typeof(IEditorWorkspaceResolver));
        var existingStore = services.LastOrDefault(d => d.ServiceType == typeof(IEditorWorkspaceStore));
        if (existingResolver is null || existingStore is null)
        {
            throw new InvalidOperationException("Call AddBazaarAthanor before AddBazaarAthanorVault.");
        }

        services.AddSingleton(options);
        services.Replace(ServiceDescriptor.Singleton<IEditorWorkspaceResolver>(sp =>
            new VaultFirstWorkspaceResolver(options, (IEditorWorkspaceResolver)Build(sp, existingResolver))));
        services.Replace(ServiceDescriptor.Singleton<IEditorWorkspaceStore>(sp => new VaultEditorWorkspaceStore(
            (IEditorWorkspaceStore)Build(sp, existingStore),
            sp.GetRequiredService<IGitProvider>(),
            sp.GetRequiredService<IVaultWriter>(),
            sp.GetRequiredService<IAthanorLimits>(),
            options)));
        return services;
    }

    private static object Build(IServiceProvider sp, ServiceDescriptor d) =>
        d.ImplementationInstance
        ?? d.ImplementationFactory?.Invoke(sp)
        ?? ActivatorUtilities.CreateInstance(sp, d.ImplementationType!);
}

/// <summary>Resolves the Vault workspace id to the checkout; every other id goes to the previous resolver.</summary>
internal sealed class VaultFirstWorkspaceResolver(VaultEditorOptions options, IEditorWorkspaceResolver next) : IEditorWorkspaceResolver
{
    public async ValueTask<EditorWorkspaceRoot?> ResolveAsync(string workspaceId, CancellationToken cancellationToken)
    {
        if (!string.Equals(workspaceId, options.WorkspaceId, StringComparison.Ordinal))
        {
            return await next.ResolveAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        }

        // The checkout is fixed by the options (saves land there); an operator entry with the same id can only make it read-only.
        var existing = await next.ResolveAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        return new EditorWorkspaceRoot(options.WorkspaceId, EditorWorkspaceKind.Vault, options.RepoPath, existing?.ReadOnly ?? false);
    }
}
