using Bazaar.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.GitHub;

/// <summary>
/// The GitHub forge provider (doc 21) as a Bazaar Component. It owns no migration namespace yet: the <c>github_*</c> tables arrive
/// with the ingress and PR service work packages. Host composition lists it in the component set and the kernel calls
/// <see cref="AddServices"/>.
/// </summary>
public sealed class GitHubComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "github",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:github", "forge:github"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddBazaarGitHub(configuration);
}
