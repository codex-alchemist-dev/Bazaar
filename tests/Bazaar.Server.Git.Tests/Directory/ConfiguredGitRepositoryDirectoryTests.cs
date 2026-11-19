using Bazaar.Server.Git.RepositoryDirectory;

namespace Bazaar.Server.Git.Tests.RepositoryDirectory;

public class ConfiguredGitRepositoryDirectoryTests
{
    private static readonly string _clone = Path.Combine(Path.GetTempPath(), "bazaar-clones", "repo");

    private static GitRepositoryEntry Entry(string id, string provider = "github", string fullName = "owner/repo", string? path = null) =>
        new() { RepositoryId = id, Provider = provider, FullName = fullName, LocalClonePath = path ?? _clone };

    private static ConfiguredGitRepositoryDirectory Build(params GitRepositoryEntry[] entries) =>
        new(new GitRepositoryDirectoryOptions { Repositories = entries });

    [Fact]
    public async Task Find_returns_the_configured_repo_for_a_known_id_ignoring_case()
    {
        var directory = Build(new GitRepositoryEntry
        {
            RepositoryId = "repo-alpha",
            Provider = "github",
            FullName = "codex-alchemist-dev/Bazaar",
            LocalClonePath = _clone,
            InstallationId = "12345",
            ProviderRepositoryId = "R_kgDO1",
        });

        var info = await directory.FindAsync("REPO-ALPHA", CancellationToken.None);

        Assert.NotNull(info);
        Assert.Equal("repo-alpha", info.RepositoryId);
        Assert.Equal(new RepoRef("github", "codex-alchemist-dev/Bazaar"), info.Repo);
        Assert.Equal(_clone, info.LocalClonePath);
        Assert.Equal("12345", info.InstallationId);
        Assert.Equal("R_kgDO1", info.ProviderRepositoryId);
    }

    [Fact]
    public async Task Find_returns_null_for_an_unknown_id()
    {
        var directory = Build(Entry("repo-alpha"));

        Assert.Null(await directory.FindAsync("repo-missing", CancellationToken.None));
        Assert.Null(await directory.FindAsync(string.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task List_returns_every_entry_ordered_by_provider_then_full_name()
    {
        var directory = Build(
            Entry("id-1", provider: "gitlab", fullName: "group/project"),
            Entry("id-2", fullName: "org/zeta"),
            Entry("id-3", fullName: "org/alpha"));

        var list = await directory.ListAsync(CancellationToken.None);

        Assert.Equal(["id-3", "id-2", "id-1"], list.Select(static r => r.RepositoryId));
    }

    [Fact]
    public async Task An_empty_configuration_lists_nothing_and_finds_nothing()
    {
        var directory = Build();

        Assert.Empty(await directory.ListAsync(CancellationToken.None));
        Assert.Null(await directory.FindAsync("any", CancellationToken.None));
    }

    [Fact]
    public void Duplicate_repository_ids_fail_at_construction_ignoring_case()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(Entry("repo-a"), Entry("REPO-A")));

        Assert.Contains("'REPO-A' is already configured", ex.Message);
    }

    [Fact]
    public void Every_problem_is_reported_in_one_exception()
    {
        var bad = new GitRepositoryEntry
        {
            RepositoryId = "repo-x",
            Provider = " ",
            FullName = "owner/repo",
            LocalClonePath = "relative/clone",
        };

        var ex = Assert.Throws<InvalidOperationException>(() => Build(bad, Entry("repo-x")));

        Assert.Contains("Repositories[0]: Provider is required.", ex.Message);
        Assert.Contains("Repositories[0]: LocalClonePath 'relative/clone' must be an absolute path.", ex.Message);
        Assert.Contains("Repositories[1]: RepositoryId 'repo-x' is already configured.", ex.Message);
    }

    [Fact]
    public void A_relative_clone_path_is_rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(Entry("repo-a", path: "clones/repo")));

        Assert.Contains("must be an absolute path", ex.Message);
    }

    [Fact]
    public void An_uppercase_provider_id_is_rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(Entry("repo-a", provider: "GitHub")));

        Assert.Contains("Provider 'GitHub' must be lowercase", ex.Message);
    }

    [Theory]
    [InlineData("noslash")]
    [InlineData("owner/")]
    [InlineData("/repo")]
    public void A_full_name_without_an_owner_and_a_name_is_rejected(string fullName)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(Entry("repo-a", fullName: fullName)));

        Assert.Contains("must be owner/name", ex.Message);
    }

    [Fact]
    public async Task A_nested_gitlab_owner_is_kept_as_the_full_project_path()
    {
        var directory = Build(Entry("repo-deep", provider: "gitlab", fullName: "group/subgroup/project"));

        var info = await directory.FindAsync("repo-deep", CancellationToken.None);

        Assert.NotNull(info);
        Assert.Equal(new RepoRef("gitlab", "group/subgroup/project"), info.Repo);
    }

    [Fact]
    public async Task Blank_optional_ids_become_null()
    {
        var directory = Build(new GitRepositoryEntry
        {
            RepositoryId = "repo-a",
            Provider = "github",
            FullName = "owner/repo",
            LocalClonePath = _clone,
            InstallationId = "  ",
            ProviderRepositoryId = string.Empty,
        });

        var info = await directory.FindAsync("repo-a", CancellationToken.None);

        Assert.NotNull(info);
        Assert.Null(info.InstallationId);
        Assert.Null(info.ProviderRepositoryId);
    }

    [Fact]
    public async Task A_cancelled_lookup_throws()
    {
        var directory = Build(Entry("repo-a"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => directory.ListAsync(cts.Token));
    }
}
