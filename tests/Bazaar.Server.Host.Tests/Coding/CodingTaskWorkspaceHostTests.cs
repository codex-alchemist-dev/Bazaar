using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingGit.Fakes;
using Bazaar.Server.CodingWorkspace;
using Bazaar.Server.CodingWorkspace.Fakes;
using Bazaar.Server.GitHub.Workspace;
using Bazaar.Server.Host.Coding;
using Bazaar.Server.Vault.Git;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Repository = LibGit2Sharp.Repository;

namespace Bazaar.Server.Host.Tests.Coding;

public sealed class CodingTaskWorkspaceHostTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "bazaar-taskws-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_base))
            {
                foreach (var file in Directory.EnumerateFiles(_base, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(_base, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort: a temp folder left behind is harmless.
        }
    }

    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => p.Value)).Build();

    [Fact]
    public void Nothing_is_registered_or_checked_while_the_harness_is_off()
    {
        var services = new ServiceCollection();

        services.AddBazaarCodingTaskWorkspace(Config(("Coding:Harness:Enabled", "false")));
        services.AddBazaarCodingTaskWorkspace(null);

        Assert.Empty(services);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("bad id")]
    [InlineData("trailing.")]
    public void Enabled_with_a_missing_or_bad_repo_stops_the_host_naming_the_key(string? repo)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddBazaarCodingTaskWorkspace(Config(("Coding:Harness:Enabled", "true"), ("Coding:Harness:Repo", repo))));

        Assert.Contains("Coding:Harness:Repo", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("bad id", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Enabled_with_a_repo_registers_the_workspace_and_a_shared_registry()
    {
        var services = new ServiceCollection();

        services.AddBazaarCodingTaskWorkspace(Config(("Coding:Harness:Enabled", "true"), ("Coding:Harness:Repo", "bazaar")));

        Assert.Contains(services, d => d.ServiceType == typeof(ICodingTaskWorkspace));
        Assert.Contains(services, d => d.ServiceType == typeof(ICodingWorktreeRegistrar));
        Assert.Contains(services, d => d.ServiceType == typeof(ICodingWorktreeDirectory));
    }

    private static string InitClone(string path)
    {
        Directory.CreateDirectory(path);
        Repository.Init(path);
        using var repo = new Repository(path);
        File.WriteAllText(Path.Combine(path, "a.txt"), "one\n");
        LibGit2Sharp.Commands.Stage(repo, "a.txt");
        var sig = new LibGit2Sharp.Signature("bot", "bot@example.invalid", DateTimeOffset.UtcNow);
        LibGit2Sharp.RepositoryExtensions.Commit(repo, "base", sig, sig);
        return path;
    }

    private (CodingTaskWorkspace Workspace, CodingWorktreeRegistry Registry, string Root) RealFlow()
    {
        var root = Path.Combine(_base, "root");
        var clone = InitClone(Path.Combine(_base, "clone"));
        var options = new CodingWorkspaceOptions { Root = root };
        var layout = new WorkShareLayout(new ConfiguredAgentWorkShareSource(options));
        var repos = new InMemoryCodingRepoSource();
        repos.Set(new CodingRepoRecord(new RepoRef("bazaar", "main"), clone, 11, "owner", "Bazaar", 99));
        var workspace = new GitWorkspace(new LibGit2GitProvider(), new GitWorkspaceOptions { RootPath = root }, new WorkShareLayoutRootResolver(layout));
        var engine = new GitWorkspaceCodingGitEngine(workspace, root, new CodingRepoDirectoryAdapter(repos), layout);
        var service = new CodingGitService(layout, engine, new FakeHostPushPort(), new FakePushGate(), new FakeAgentGitIdentity(), new WorktreeLocks());
        var registry = new CodingWorktreeRegistry();
        return (new CodingTaskWorkspace(service, repos, layout, new CodingWorktreeRegistryRegistrar(registry), "bazaar"), registry, root);
    }

    [Fact]
    public async Task A_real_repo_gets_a_task_worktree_on_its_branch_registered_under_the_key()
    {
        var (workspace, registry, root) = RealFlow();

        var open = await workspace.OpenAsync("charles", "fix-login", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("agent/fix-login", open.Branch);
        Assert.True(File.Exists(Path.Combine(open.Path, "a.txt")));
        Assert.True(WorkSharePaths.IsEqualOrUnder(open.Path, Path.Combine(root, "agents", "charles", "work")));
        using (var repo = new Repository(open.Path))
        {
            Assert.Equal("agent/fix-login", repo.Head.FriendlyName);
        }

        var registered = registry.Find(open.WorktreeKey);
        Assert.NotNull(registered);
        Assert.Equal(open.Path, registered.Path);
        Assert.Null(registry.Find("fix-login"));
    }

    [Fact]
    public async Task A_real_second_open_close_and_discard_behave()
    {
        var (workspace, registry, _) = RealFlow();
        var open = await workspace.OpenAsync("charles", "t1", cancellationToken: TestContext.Current.CancellationToken);

        var again = await Assert.ThrowsAsync<CodingTaskWorkspaceException>(() => workspace.OpenAsync("charles", "t1", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(CodingTaskWorkspaceError.AlreadyExists, again.Error);

        await workspace.CloseAsync(open.WorktreeKey, TestContext.Current.CancellationToken);
        Assert.Null(registry.Find(open.WorktreeKey));
        Assert.True(Directory.Exists(open.Path));

        // The directory is still there, so reopening is refused rather than silently reused.
        var reopened = await Assert.ThrowsAsync<CodingTaskWorkspaceException>(() => workspace.OpenAsync("charles", "t1", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(CodingTaskWorkspaceError.AlreadyExists, reopened.Error);

        var second = await workspace.OpenAsync("charles", "t2", cancellationToken: TestContext.Current.CancellationToken);
        await workspace.DiscardAsync(second.WorktreeKey, TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists(second.Path));
        Assert.Null(registry.Find(second.WorktreeKey));
    }

    [Fact]
    public async Task A_real_unknown_repo_and_unsafe_task_are_refused()
    {
        var (workspace, registry, root) = RealFlow();

        var repo = await Assert.ThrowsAsync<CodingTaskWorkspaceException>(() => workspace.OpenAsync("charles", "t1", "nope", cancellationToken: TestContext.Current.CancellationToken));
        var task = await Assert.ThrowsAsync<CodingTaskWorkspaceException>(() => workspace.OpenAsync("charles", "../escape", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(CodingTaskWorkspaceError.UnknownRepo, repo.Error);
        Assert.Equal(CodingTaskWorkspaceError.InvalidTask, task.Error);
        Assert.False(Directory.Exists(root));
        Assert.Null(registry.Find("t1"));
    }
}
