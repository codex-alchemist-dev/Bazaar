using Cdax.Core;
using Bazaar.Server.Git.GitLab.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Git.GitLab.Tests;

public class GitLabRegistrationTests
{
    [Fact]
    public void AddBazaarGitLab_registers_the_provider_and_the_merge_executor()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGitCredentialSource>(new FakeCredentialSource());
        services.AddBazaarGitLab(options => options.MaxRetries = 1);

        using var provider = services.BuildServiceProvider();

        var forge = provider.GetRequiredService<IForgeProvider>();
        var executor = provider.GetRequiredService<IForgeMergeExecutor>();
        Assert.IsType<GitLabForgeProvider>(forge);
        Assert.IsType<GitLabMergeExecutor>(executor);
        Assert.Equal("gitlab", forge.ProviderId);
        Assert.Equal("gitlab", executor.ProviderId);
    }

    [Fact]
    public void AddBazaarGitLab_needs_a_credential_source_from_the_host()
    {
        var services = new ServiceCollection();
        services.AddBazaarGitLab();

        using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IForgeProvider>());
    }

    [Fact]
    public void The_component_manifest_is_git_gitlab_and_depends_on_git()
    {
        var component = new GitLabComponent();

        Assert.Equal("git-gitlab", component.Manifest.Id);
        Assert.Equal(ComponentKind.Provider, component.Manifest.Kind);
        Assert.Equal(["git"], component.Manifest.DependsOn);
        Assert.Equal(["git:forge:gitlab"], component.Manifest.Provides);
    }

    [Fact]
    public void The_component_registers_the_gitlab_provider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGitCredentialSource>(new FakeCredentialSource());

        new GitLabComponent().AddServices(services, new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        Assert.IsType<GitLabForgeProvider>(provider.GetRequiredService<IForgeProvider>());
    }
}
