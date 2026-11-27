using Cdax.Core;
using Bazaar.Server.Git.GitHub.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Git.GitHub.Tests;

public class GitHubRegistrationTests
{
    [Fact]
    public void AddBazaarGitHub_registers_the_provider_and_the_merge_executor()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGitCredentialSource>(new FakeCredentialSource());
        services.AddBazaarGitHub(options => options.MaxRetries = 1);

        using var provider = services.BuildServiceProvider();

        var forge = provider.GetRequiredService<IForgeProvider>();
        var executor = provider.GetRequiredService<IForgeMergeExecutor>();
        Assert.IsType<GitHubForgeProvider>(forge);
        Assert.IsType<GitHubMergeExecutor>(executor);
        Assert.Equal("github", forge.ProviderId);
        Assert.Equal("github", executor.ProviderId);
    }

    [Fact]
    public void AddBazaarGitHub_needs_a_credential_source_from_the_host()
    {
        var services = new ServiceCollection();
        services.AddBazaarGitHub();

        using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IForgeProvider>());
    }

    [Fact]
    public void The_component_manifest_is_git_github_and_depends_on_git()
    {
        var component = new GitHubComponent();

        Assert.Equal("git-github", component.Manifest.Id);
        Assert.Equal(ComponentKind.Provider, component.Manifest.Kind);
        Assert.Equal(["git"], component.Manifest.DependsOn);
        Assert.Equal(["git:forge:github"], component.Manifest.Provides);
    }

    [Fact]
    public void The_component_registers_the_github_provider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGitCredentialSource>(new FakeCredentialSource());

        new GitHubComponent().AddServices(services, new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        Assert.IsType<GitHubForgeProvider>(provider.GetRequiredService<IForgeProvider>());
    }
}
