using Cdax.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Analytics.Git;

/// <summary>
/// Registers the forge merge collector. Settings: <c>Bazaar:Analytics:MergeSource</c> (<c>Kanban</c>, the default, or <c>Forge</c>) and
/// <c>Bazaar:Analytics:ForgeAuthors:{login}</c> = agent id. Exactly one source counts merges, so a merge is never counted twice. With
/// <c>Forge</c> the Postgres module drops its merge events.
/// </summary>
public sealed class AnalyticsGitComponent : ICdaxModule
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "analytics-git",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["analytics"],
        Provides: ["analytics:collector:forge-merges"],
        SoftDependsOn: ["analytics-postgres"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        if (!UsesForge(configuration))
        {
            return;
        }

        var map = configuration.GetSection("Bazaar:Analytics:ForgeAuthors").GetChildren()
            .Where(c => !string.IsNullOrWhiteSpace(c.Value))
            .ToDictionary(c => c.Key, c => c.Value!.Trim(), StringComparer.OrdinalIgnoreCase);
        services.TryAddSingleton<IForgeAuthorResolver>(new ConfiguredForgeAuthorResolver(map));
        services.AddSingleton<IWorkEventCollector>(sp => new ForgeMergeCollector(
            sp.GetRequiredService<Bazaar.Server.Git.IGitRepositoryDirectory>(),
            sp.GetRequiredService<Bazaar.Server.Git.IForgeRegistry>(),
            sp.GetRequiredService<IForgeAuthorResolver>()));
    }

    public static bool UsesForge(IConfiguration configuration) =>
        string.Equals(configuration["Bazaar:Analytics:MergeSource"], "Forge", StringComparison.OrdinalIgnoreCase);
}
