using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.CodingGit;

/// <summary>Registration for the worktree and push service.</summary>
public static class CodingGitServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="CodingGitService"/>, the in-process <see cref="IWorktreeLocks"/> and
    /// <see cref="CodingGitOptions"/>. The host registers the ports: <see cref="IWorkShareLayout"/>,
    /// <see cref="ICodingGitEngine"/>, <see cref="IHostPushPort"/>, <see cref="IPushGate"/> and <see cref="IAgentGitIdentity"/>.
    /// </summary>
    public static IServiceCollection AddBazaarCodingGit(this IServiceCollection services, Action<CodingGitOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new CodingGitOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IWorktreeLocks>(sp => new WorktreeLocks(sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<CodingGitService>();
        return services;
    }
}
