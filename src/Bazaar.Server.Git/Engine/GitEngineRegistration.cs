using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Git;

public static class GitEngineRegistration
{
    /// <summary>
    /// Registers the LibGit2 <see cref="IGitProvider"/> and its <see cref="RepoLocks"/> (doc 52). The host supplies
    /// <see cref="IGitCredentialSource"/>. Existing registrations win (TryAdd).
    /// </summary>
    public static IServiceCollection AddBazaarGitEngine(this IServiceCollection services, Action<GitEngineOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<GitEngineOptions>().Configure(configure);
        services.TryAddSingleton(static sp => new RepoLocks(sp.GetRequiredService<IOptions<GitEngineOptions>>().Value.RepoLockTimeout));
        services.TryAddSingleton<IGitProvider>(static sp => new LibGit2GitProvider(
            sp.GetRequiredService<IOptions<GitEngineOptions>>().Value,
            sp.GetRequiredService<IGitCredentialSource>(),
            sp.GetRequiredService<RepoLocks>()));
        services.TryAddSingleton<IGitHistoryReader>(static sp => new LibGit2HistoryReader(
            sp.GetRequiredService<IOptions<GitEngineOptions>>().Value,
            sp.GetRequiredService<RepoLocks>()));
        return services;
    }
}
