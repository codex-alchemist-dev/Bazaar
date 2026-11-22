using Bazaar.Server.CodingGit;
using Bazaar.Server.Subagents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.CodingWorkspace;

/// <summary>Registration for the coding workspace sources.</summary>
public static class CodingWorkspaceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shipped sources with TryAdd, so an earlier registration (the VM mounts share source, the Git module's
    /// repository directory) wins: <see cref="IAgentWorkShareSource"/>, <see cref="IWorkShareLayout"/>, <see cref="ICodingRepoSource"/>
    /// and <see cref="ISubagentWorktreeAllocator"/>. <see cref="ISubagentParentWorkSource"/> is not registered: the host binds it.
    /// <see cref="CodingGitService"/> must be registered (<c>AddBazaarCodingGit</c>) before the allocator is resolved.
    /// </summary>
    public static IServiceCollection AddBazaarCodingWorkspace(this IServiceCollection services, Action<CodingWorkspaceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new CodingWorkspaceOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<IAgentWorkShareSource>(sp => new ConfiguredAgentWorkShareSource(sp.GetRequiredService<CodingWorkspaceOptions>()));
        services.TryAddSingleton<IWorkShareLayout>(sp => new WorkShareLayout(sp.GetRequiredService<IAgentWorkShareSource>()));
        services.TryAddSingleton<ICodingRepoSource>(sp => new ConfiguredCodingRepoSource(sp.GetRequiredService<CodingWorkspaceOptions>()));
        services.TryAddSingleton<ISubagentWorktreeAllocator>(sp => new GitSubagentWorktreeAllocator(
            sp.GetRequiredService<CodingGitService>(),
            sp.GetRequiredService<IWorkShareLayout>(),
            sp.GetRequiredService<ISubagentParentWorkSource>()));
        return services;
    }
}
