using Bazaar.Server.Git.RepositoryDirectory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Git.Tests.RepositoryDirectory;

public class GitRepositoryDirectoryRegistrationTests
{
    private static readonly string _clone = Path.Combine(Path.GetTempPath(), "bazaar-clones", "repo");

    [Fact]
    public async Task Binds_the_repos_from_the_configuration_section()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:Git:Repositories:0:RepositoryId"] = "repo-alpha",
            ["Bazaar:Git:Repositories:0:Provider"] = "github",
            ["Bazaar:Git:Repositories:0:FullName"] = "codex-alchemist-dev/Bazaar",
            ["Bazaar:Git:Repositories:0:LocalClonePath"] = _clone,
            ["Bazaar:Git:Repositories:0:InstallationId"] = "98765",
            ["Bazaar:Git:Repositories:1:RepositoryId"] = "repo-beta",
            ["Bazaar:Git:Repositories:1:Provider"] = "gitlab",
            ["Bazaar:Git:Repositories:1:FullName"] = "group/sub/project",
            ["Bazaar:Git:Repositories:1:LocalClonePath"] = _clone,
            ["Bazaar:Git:Repositories:1:ProviderRepositoryId"] = "gl-42",
        }).Build();
        var services = new ServiceCollection();

        services.AddBazaarGitRepositoryDirectory(configuration);
        await using var provider = services.BuildServiceProvider();
        var directory = provider.GetRequiredService<IGitRepositoryDirectory>();

        var alpha = await directory.FindAsync("repo-alpha", CancellationToken.None);
        var beta = await directory.FindAsync("repo-beta", CancellationToken.None);

        Assert.NotNull(alpha);
        Assert.Equal(new RepoRef("github", "codex-alchemist-dev/Bazaar"), alpha.Repo);
        Assert.Equal("98765", alpha.InstallationId);
        Assert.NotNull(beta);
        Assert.Equal(new RepoRef("gitlab", "group/sub/project"), beta.Repo);
        Assert.Equal("gl-42", beta.ProviderRepositoryId);
        Assert.Equal(2, (await directory.ListAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task A_missing_section_or_a_null_configuration_yields_an_empty_directory()
    {
        var empty = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddBazaarGitRepositoryDirectory(empty);
        services.AddBazaarGitRepositoryDirectory();
        await using var provider = services.BuildServiceProvider();

        var directory = provider.GetRequiredService<IGitRepositoryDirectory>();

        Assert.Empty(await directory.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_pre_registered_directory_stays_in_place()
    {
        var store = new ConfiguredGitRepositoryDirectory(new GitRepositoryDirectoryOptions
        {
            Repositories = [new GitRepositoryEntry { RepositoryId = "store-repo", Provider = "github", FullName = "o/r", LocalClonePath = _clone }],
        });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:Git:Repositories:0:RepositoryId"] = "config-repo",
            ["Bazaar:Git:Repositories:0:Provider"] = "github",
            ["Bazaar:Git:Repositories:0:FullName"] = "o/c",
            ["Bazaar:Git:Repositories:0:LocalClonePath"] = _clone,
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IGitRepositoryDirectory>(store);

        services.AddBazaarGitRepositoryDirectory(configuration);
        await using var provider = services.BuildServiceProvider();

        Assert.Same(store, provider.GetRequiredService<IGitRepositoryDirectory>());
        Assert.Null(await store.FindAsync("config-repo", CancellationToken.None));
    }

    [Fact]
    public void Adding_the_directory_does_not_validate_the_configuration_until_it_is_resolved()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:Git:Repositories:0:RepositoryId"] = "repo-a",
            ["Bazaar:Git:Repositories:0:Provider"] = "github",
            ["Bazaar:Git:Repositories:0:FullName"] = "owner/repo",
            ["Bazaar:Git:Repositories:0:LocalClonePath"] = "relative/path",
        }).Build();
        var services = new ServiceCollection();

        services.AddBazaarGitRepositoryDirectory(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IGitRepositoryDirectory>());
    }

    [Fact]
    public void The_git_component_registers_the_fallback_directory()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:Git:Repositories:0:RepositoryId"] = "repo-a",
            ["Bazaar:Git:Repositories:0:Provider"] = "github",
            ["Bazaar:Git:Repositories:0:FullName"] = "owner/repo",
            ["Bazaar:Git:Repositories:0:LocalClonePath"] = _clone,
        }).Build();
        var services = new ServiceCollection();

        services.AddBazaarGit(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<ConfiguredGitRepositoryDirectory>(provider.GetRequiredService<IGitRepositoryDirectory>());
    }
}
