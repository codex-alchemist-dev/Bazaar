using Bazaar.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Git.GitHub;

/// <summary>The GitHub forge provider as a Bazaar Component (doc 52). It needs the <c>git</c> component and adds no other dependency.</summary>
public sealed class GitHubComponent : IBazaarComponent
{
    public const string ComponentId = "git-github";

    public ComponentManifest Manifest { get; } = new(
        ComponentId,
        "0.1.0",
        ComponentKind.Provider,
        ["git"],
        ["git:forge:github"]);

    public void AddServices(IServiceCollection services, IConfiguration configuration) => services.AddBazaarGitHub();
}
