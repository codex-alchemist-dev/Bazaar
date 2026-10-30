using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.GitHub;

/// <summary>
/// Registration for the GitHub component (doc 21). Only host composition calls <see cref="AddBazaarGitHub"/>.
/// TODO(component): switch to the Bazaar Component model once PR #125 merges.
/// </summary>
public static class GitHubComponent
{
    public static IServiceCollection AddBazaarGitHub(this IServiceCollection services) => services;
}
