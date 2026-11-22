using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Git.GitLab;

/// <summary>
/// Registration for the GitLab provider (doc 52). It adds <see cref="IForgeProvider"/> and <see cref="IForgeMergeExecutor"/>. The
/// credential seam (<see cref="IGitCredentialSource"/>) is host composition's job (doc 14) and is not registered here.
/// </summary>
public static class GitLabRegistration
{
    /// <summary>
    /// Adds the GitLab forge provider and merge executor. The HTTP client is one process-lifetime instance owned by this
    /// registration, because the package set has no <c>Microsoft.Extensions.Http</c> factory.
    /// </summary>
    public static IServiceCollection AddBazaarGitLab(this IServiceCollection services, Action<GitLabOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new GitLabOptions();
        configure?.Invoke(options);
        var http = new HttpClient();

        services.AddSingleton<IForgeProvider>(sp => new GitLabForgeProvider(
            http,
            sp.GetRequiredService<IGitCredentialSource>(),
            options,
            sp.GetService<TimeProvider>()));
        services.AddSingleton<IForgeMergeExecutor>(sp => new GitLabMergeExecutor(
            http,
            sp.GetRequiredService<IGitCredentialSource>(),
            options,
            sp.GetService<TimeProvider>()));
        return services;
    }
}
