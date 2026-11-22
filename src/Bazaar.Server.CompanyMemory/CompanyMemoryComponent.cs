using Bazaar.Core;
using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Writes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.CompanyMemory;

/// <summary>
/// Company Memory (docs/design/25-company-systems-knowledge.md, docs/design/08-memory-system.md section 6) as a Bazaar
/// Component. Institutional memory and decision chains live as Markdown in the Vault; this module records decisions through
/// the Vault writer and rebuilds the chain from the files. Host composition is the only caller of <see cref="AddServices"/>.
/// </summary>
/// <remarks>
/// Vault is a soft dependency. When the host registers an <see cref="IGitProvider"/> and a <see cref="VaultWriter"/> and the
/// <c>Bazaar:CompanyMemory:VaultRepoPath</c> setting names the Vault checkout, decisions go to the Vault. Otherwise the
/// in-memory store keeps them for the life of the process.
/// </remarks>
public sealed class CompanyMemoryComponent : IBazaarComponent
{
    /// <summary>Configuration section for this module.</summary>
    public const string SectionName = "Bazaar:CompanyMemory";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "company-memory",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:company-memory"],
        SoftDependsOn: ["vault"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var repoPath = configuration.GetSection(SectionName)["VaultRepoPath"];

        services.TryAddSingleton<ICompanyDecisionStore>(sp =>
            !string.IsNullOrWhiteSpace(repoPath)
            && sp.GetService<IGitProvider>() is { } git
            && sp.GetService<VaultWriter>() is { } writer
                ? new VaultCompanyDecisionStore(git, writer, repoPath)
                : new InMemoryCompanyDecisionStore());
        services.TryAddSingleton<ICompanyMemory>(sp => new CompanyMemoryService(sp.GetRequiredService<ICompanyDecisionStore>()));
    }
}
