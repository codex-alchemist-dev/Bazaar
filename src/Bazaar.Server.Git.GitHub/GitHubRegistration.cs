using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Git.GitHub;

/// <summary>
/// Registration for the GitHub provider (doc 52). It adds <see cref="IForgeProvider"/> and <see cref="IForgeMergeExecutor"/>. The
/// credential seam (<see cref="IGitCredentialSource"/>) is host composition's job (doc 14) and is not registered here.
/// </summary>
public static class GitHubRegistration
{
    /// <summary>
    /// Adds the GitHub forge provider and merge executor. The HTTP client is one process-lifetime instance owned by this
    /// registration, because the package set has no <c>Microsoft.Extensions.Http</c> factory.
    /// </summary>
    public static IServiceCollection AddBazaarGitHub(this IServiceCollection services, Action<GitHubOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new GitHubOptions();
        configure?.Invoke(options);
        var http = new HttpClient();

        services.AddSingleton<IForgeProvider>(sp => new GitHubForgeProvider(
            http,
            sp.GetRequiredService<IGitCredentialSource>(),
            options,
            sp.GetService<TimeProvider>()));
        services.AddSingleton<IForgeMergeExecutor>(sp => new GitHubMergeExecutor(
            http,
            sp.GetRequiredService<IGitCredentialSource>(),
            options,
            sp.GetService<TimeProvider>()));
        return services;
    }
}
