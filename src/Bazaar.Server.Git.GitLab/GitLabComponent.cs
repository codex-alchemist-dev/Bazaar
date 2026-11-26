using Bazaar.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Git.GitLab;

/// <summary>The GitLab forge provider as a Bazaar Component (doc 52). It needs the <c>git</c> component and adds no other dependency.</summary>
public sealed class GitLabComponent : IBazaarComponent
{
    public const string ComponentId = "git-gitlab";

    public ComponentManifest Manifest { get; } = new(
        ComponentId,
        "0.1.0",
        ComponentKind.Provider,
        ["git"],
        ["git:forge:gitlab"]);

    public void AddServices(IServiceCollection services, IConfiguration configuration) => services.AddBazaarGitLab();
}
