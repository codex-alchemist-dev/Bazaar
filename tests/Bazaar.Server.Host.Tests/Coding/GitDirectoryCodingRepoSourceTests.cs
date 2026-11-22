using Bazaar.Server.CodingWorkspace;
using Bazaar.Server.Git;
using Bazaar.Server.Host.Coding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests.Coding;

public sealed class GitDirectoryCodingRepoSourceTests : IDisposable
{
    private readonly string _clone = Path.Combine(Path.GetTempPath(), "bazaar-gitdir-" + Guid.NewGuid().ToString("N"));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-gitdir-root-" + Guid.NewGuid().ToString("N"));

    public GitDirectoryCodingRepoSourceTests() => Directory.CreateDirectory(_clone);

    public void Dispose() => Directory.Delete(_clone, recursive: true);

    private GitRepositoryInfo Info(
        string id = "bazaar", string provider = "github", string fullName = "owner/Bazaar", string? path = null,
        string? installation = "11", string? providerId = "99") =>
        new(id, new RepoRef(provider, fullName), path ?? _clone, installation, providerId);

    private GitDirectoryCodingRepoSource Source(FakeDirectory directory, string? root = null, string branch = "main") =>
        new(directory, new CodingWorkspaceOptions { Root = root ?? _root }, branch);

    [Fact]
    public async Task A_valid_github_repo_maps_to_a_record()
    {
        var record = await Source(new FakeDirectory(Info()), branch: "trunk").FindAsync("bazaar", CancellationToken.None);

        Assert.NotNull(record);
        Assert.Equal("bazaar", record.Repo.Id);
        Assert.Equal("trunk", record.Repo.DefaultBranch);
        Assert.Equal(Path.GetFullPath(_clone), record.ClonePath);
        Assert.Equal(11, record.InstallationId);
        Assert.Equal("owner", record.Owner);
        Assert.Equal("Bazaar", record.Name);
        Assert.Equal(99, record.RepositoryId);
    }

    [Fact]
    public async Task An_unknown_repo_is_null()
    {
        Assert.Null(await Source(new FakeDirectory(Info())).FindAsync("other", CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("CON")]
    public async Task An_unsafe_id_is_null_and_never_reaches_the_directory(string id)
    {
        var directory = new FakeDirectory(Info(id: id));

        Assert.Null(await Source(directory).FindAsync(id, CancellationToken.None));
        Assert.Equal(0, directory.Calls);
    }

    [Theory]
    [InlineData("gitlab", "owner/name", "11", "99")]
    [InlineData("github", "name", "11", "99")]
    [InlineData("github", "a/b/c", "11", "99")]
    [InlineData("github", "own er/name", "11", "99")]
    [InlineData("github", "owner/name", null, "99")]
    [InlineData("github", "owner/name", "abc", "99")]
    [InlineData("github", "owner/name", "0", "99")]
    [InlineData("github", "owner/name", "-5", "99")]
    [InlineData("github", "owner/name", "11", null)]
    [InlineData("github", "owner/name", "11", "MDEwOlJlcG9zaXRvcnkx")]
    public async Task A_repo_the_coding_port_cannot_use_is_null(string provider, string fullName, string? installation, string? providerId)
    {
        var directory = new FakeDirectory(Info(provider: provider, fullName: fullName, installation: installation, providerId: providerId));

        Assert.Null(await Source(directory).FindAsync("bazaar", CancellationToken.None));
    }

    [Fact]
    public async Task A_missing_or_relative_clone_is_null()
    {
        var gone = Path.Combine(Path.GetTempPath(), "bazaar-gitdir-gone-" + Guid.NewGuid().ToString("N"));

        Assert.Null(await Source(new FakeDirectory(Info(path: gone))).FindAsync("bazaar", CancellationToken.None));
        Assert.Null(await Source(new FakeDirectory(Info(path: "relative/clone"))).FindAsync("bazaar", CancellationToken.None));
    }

    [Fact]
    public async Task A_clone_on_or_around_the_work_share_root_is_null()
    {
        var inside = Path.Combine(_clone, "work");
        Directory.CreateDirectory(inside);

        // The clone sits inside the root, and the root sits inside the clone.
        Assert.Null(await Source(new FakeDirectory(Info()), root: _clone).FindAsync("bazaar", CancellationToken.None));
        Assert.Null(await Source(new FakeDirectory(Info()), root: inside).FindAsync("bazaar", CancellationToken.None));
        Assert.NotNull(await Source(new FakeDirectory(Info()), root: _root).FindAsync("bazaar", CancellationToken.None));
    }

    [Fact]
    public async Task A_directory_that_returns_another_repo_or_throws_is_null()
    {
        Assert.Null(await Source(new FakeDirectory(Info(id: "different")) { AnswerAnyId = true }).FindAsync("bazaar", CancellationToken.None));
        Assert.Null(await Source(new FakeDirectory(Info()) { Throw = true }).FindAsync("bazaar", CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Source(new FakeDirectory(Info())).FindAsync("bazaar", cts.Token));
    }

    [Fact]
    public void An_invalid_default_branch_stops_construction()
    {
        Assert.Throws<InvalidOperationException>(() => Source(new FakeDirectory(Info()), branch: "-x"));
        Assert.Throws<InvalidOperationException>(() => Source(new FakeDirectory(Info()), branch: " "));
    }

    [Fact]
    public async Task The_host_binds_the_git_directory_when_it_is_registered()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGitRepositoryDirectory>(new FakeDirectory(Info()));
        services.AddBazaarCodingWorkspaceSources(Configuration());

        using var provider = services.BuildServiceProvider();

        Assert.IsType<GitDirectoryCodingRepoSource>(provider.GetRequiredService<ICodingRepoSource>());
        var record = await provider.GetRequiredService<ICodingRepoSource>().FindAsync("bazaar", CancellationToken.None);
        Assert.Equal("develop", record?.Repo.DefaultBranch);
        Assert.Equal(99, (await provider.GetRequiredService<ICodingRepoDirectory>().FindAsync("bazaar", CancellationToken.None))?.RepositoryId);
    }

    [Fact]
    public async Task The_registration_order_does_not_matter()
    {
        var services = new ServiceCollection();
        services.AddBazaarCodingWorkspaceSources(Configuration());
        services.AddSingleton<IGitRepositoryDirectory>(new FakeDirectory(Info()));

        using var provider = services.BuildServiceProvider();

        Assert.IsType<GitDirectoryCodingRepoSource>(provider.GetRequiredService<ICodingRepoSource>());
        Assert.NotNull(await provider.GetRequiredService<ICodingRepoSource>().FindAsync("bazaar", CancellationToken.None));
    }

    [Fact]
    public async Task Without_a_git_directory_the_configured_source_is_the_fallback()
    {
        var services = new ServiceCollection();
        services.AddBazaarCodingWorkspaceSources(Configuration(withRepos: true));

        using var provider = services.BuildServiceProvider();

        Assert.IsType<ConfiguredCodingRepoSource>(provider.GetRequiredService<ICodingRepoSource>());
        Assert.Equal(7, (await provider.GetRequiredService<ICodingRepoSource>().FindAsync("configured", CancellationToken.None))?.RepositoryId);
    }

    [Fact]
    public async Task With_a_git_directory_an_unknown_repo_does_not_fall_back_to_the_configured_list()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGitRepositoryDirectory>(new FakeDirectory(Info()));
        services.AddBazaarCodingWorkspaceSources(Configuration(withRepos: true));

        using var provider = services.BuildServiceProvider();

        Assert.Null(await provider.GetRequiredService<ICodingRepoSource>().FindAsync("configured", CancellationToken.None));
    }

    private IConfiguration Configuration(bool withRepos = false)
    {
        var values = new Dictionary<string, string?>
        {
            ["Coding:Harness:Enabled"] = "true",
            ["Coding:Workspace:Root"] = _root,
            ["Coding:Workspace:DefaultBranch"] = "develop",
        };
        if (withRepos)
        {
            values["Coding:Workspace:Repos:0:Id"] = "configured";
            values["Coding:Workspace:Repos:0:ClonePath"] = _clone;
            values["Coding:Workspace:Repos:0:InstallationId"] = "5";
            values["Coding:Workspace:Repos:0:Owner"] = "owner";
            values["Coding:Workspace:Repos:0:Name"] = "Configured";
            values["Coding:Workspace:Repos:0:RepositoryId"] = "7";
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private sealed class FakeDirectory(GitRepositoryInfo info) : IGitRepositoryDirectory
    {
        public int Calls { get; private set; }

        public bool AnswerAnyId { get; init; }

        public bool Throw { get; init; }

        public Task<GitRepositoryInfo?> FindAsync(string repositoryId, CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            if (Throw)
            {
                throw new InvalidOperationException("store down");
            }

            return Task.FromResult<GitRepositoryInfo?>(AnswerAnyId || string.Equals(info.RepositoryId, repositoryId, StringComparison.OrdinalIgnoreCase) ? info : null);
        }

        public Task<IReadOnlyList<GitRepositoryInfo>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GitRepositoryInfo>>([info]);
    }
}
