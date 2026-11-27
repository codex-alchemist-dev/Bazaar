using Cdax.Core;
using Bazaar.Server.Git.Forge;
using Bazaar.Server.Git.Health;
using Bazaar.Server.Git.Merge;
using Bazaar.Server.Git.Reads;
using Bazaar.Server.Git.RepositoryDirectory;
using Bazaar.Server.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Git;

/// <summary>
/// Bazaar.Server.Git as a Bazaar Component (doc 52): the forge registry, the owner-signed merge gate and the read service.
/// Provider modules (GitHub, GitLab) register their <see cref="IForgeProvider"/> and <see cref="IForgeMergeExecutor"/>. The gate
/// needs an <c>IAuditLog</c> and an <c>IOwnerSignatureVerifier</c> from the host; audit is a soft dependency for start order.
/// </summary>
public sealed class GitServerComponent : ICdaxModule
{
    /// <summary>Configuration section for the git settings (doc 52, Settings).</summary>
    public const string SectionName = "Bazaar:Git";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "git",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:git"],
        SoftDependsOn: ["audit"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration) => services.AddBazaarGit(configuration);
}

/// <summary>Composition for Bazaar.Server.Git (doc 52).</summary>
public static class GitServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the forge registry, the merge gate and the read service, with settings read from <c>Bazaar:Git</c>
    /// (<c>MaxDiffBytes</c>, <c>PrPageSize</c>, <c>MergeRequiresGreenChecks</c>). Every registration is TryAdd, so a host or a
    /// test can substitute one first. Bound by hand, so there is no reflection.
    /// </summary>
    public static IServiceCollection AddBazaarGit(this IServiceCollection services, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var section = configuration?.GetSection(GitServerComponent.SectionName);
        services.TryAddSingleton(new GitReadOptions
        {
            MaxDiffBytes = ReadInt(section, "MaxDiffBytes", new GitReadOptions().MaxDiffBytes),
            PrPageSize = ReadInt(section, "PrPageSize", new GitReadOptions().PrPageSize),
        });
        services.TryAddSingleton(new GitMergeOptions
        {
            RequireGreenChecks = !bool.TryParse(section?["MergeRequiresGreenChecks"], out var green) || green,
        });

        services.AddBazaarForgeRegistry();
        services.TryAddSingleton<IOwnerSignedMergeGate, OwnerSignedMergeGate>();
        services.TryAddSingleton<IGitReadService, GitReadService>();

        // Project health feeder (doc 25): CI runs and reverts from the forge, one health project per registered repository.
        services.TryAddSingleton(new GitHealthOptions
        {
            MaxPages = ReadInt(section, "HealthMaxPages", new GitHealthOptions().MaxPages),
            PageSize = ReadInt(section, "HealthPageSize", new GitHealthOptions().PageSize),
            MaxChecksPerDay = ReadInt(section, "HealthMaxChecksPerDay", new GitHealthOptions().MaxChecksPerDay),
        });
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthFactSource, GitHealthFactSource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthProjectSource, GitHealthProjectSource>());
        services.AddBazaarGitRepositoryDirectory(configuration);
        return services;
    }

    /// <summary>Registers <see cref="IForgeRegistry"/> over every registered <see cref="IForgeProvider"/> and <see cref="IForgeMergeExecutor"/>.</summary>
    public static IServiceCollection AddBazaarForgeRegistry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IForgeRegistry, ForgeRegistry>();
        return services;
    }

    private static int ReadInt(IConfigurationSection? section, string key, int fallback) =>
        int.TryParse(section?[key], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
