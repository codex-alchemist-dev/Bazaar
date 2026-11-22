using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Git.RepositoryDirectory;

/// <summary>Composition for the operator-configured repository directory (doc 52, Repository directory).</summary>
public static class GitRepositoryDirectoryServiceCollectionExtensions
{
    /// <summary>The configuration section the repos are read from.</summary>
    public const string ConfigurationSection = "Bazaar:Git:Repositories";

    /// <summary>
    /// Registers <see cref="ConfiguredGitRepositoryDirectory"/> as <see cref="IGitRepositoryDirectory"/>, with the repos bound from
    /// <see cref="ConfigurationSection"/>. TryAdd, so a store-backed directory registered earlier (or later, with Replace) wins.
    /// The directory is built on first resolution, so a store-backed replacement never validates the fallback config.
    /// </summary>
    public static IServiceCollection AddBazaarGitRepositoryDirectory(this IServiceCollection services, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new GitRepositoryDirectoryOptions
        {
            Repositories = configuration?.GetSection(ConfigurationSection).Get<List<GitRepositoryEntry>>() ?? [],
        };
        services.TryAddSingleton<IGitRepositoryDirectory>(_ => new ConfiguredGitRepositoryDirectory(options));
        return services;
    }
}
