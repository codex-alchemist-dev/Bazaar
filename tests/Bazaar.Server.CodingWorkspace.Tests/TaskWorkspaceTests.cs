using System.Collections.Concurrent;
using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingWorkspace.Fakes;

namespace Bazaar.Server.CodingWorkspace.Tests;

public sealed class TaskWorkspaceTests
{
    private sealed class FakeRegistrar : ICodingWorktreeRegistrar
    {
        public ConcurrentDictionary<string, CodingWorktree> Items { get; } = new(StringComparer.Ordinal);

        public bool Fail { get; set; }

        public IDisposable Register(string worktreeKey, CodingWorktree worktree)
        {
            if (Fail)
            {
                throw new InvalidOperationException("registrar down");
            }

            Items[worktreeKey] = worktree;
            return new Removal(Items, worktreeKey);
        }

        private sealed class Removal(ConcurrentDictionary<string, CodingWorktree> items, string key) : IDisposable
        {
            public void Dispose() => items.TryRemove(key, out _);
        }
    }

    private sealed class Setup : IDisposable
    {
        public Setup(string? defaultRepo = "repo")
        {
            Fx = new Fixture();
            Repos = new InMemoryCodingRepoSource();
            Repos.Set(new CodingRepoRecord(Fixture.Repo, Path.Combine(Fx.Base, "clone"), 1, "o", "n", 2));
            Registrar = new FakeRegistrar();
            Workspace = new CodingTaskWorkspace(Fx.Git, Repos, Fx.Layout, Registrar, defaultRepo);
        }

        public Fixture Fx { get; }

        public InMemoryCodingRepoSource Repos { get; }

        public FakeRegistrar Registrar { get; }

        public CodingTaskWorkspace Workspace { get; }

        public void Dispose() => Fx.Dispose();
    }

    private static async Task<CodingTaskWorkspaceError> ErrorOf(Task task)
    {
        var ex = await Assert.ThrowsAsync<CodingTaskWorkspaceException>(() => task);
        return ex.Error;
    }

    [Fact]
    public async Task Open_creates_a_worktree_on_the_task_branch_under_the_agents_share_and_registers_it()
    {
        using var s = new Setup();

        var open = await s.Workspace.OpenAsync("charles", "fix-login", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("agent/fix-login", open.Branch);
        Assert.Equal(s.Fx.ParentPath("charles", "repo", "fix-login"), open.Path);
        Assert.Equal(new BranchName?(BranchName.Parse("agent/fix-login")), s.Fx.Engine.BranchAt(open.Path));
        Assert.Equal(open.Path, s.Registrar.Items[open.WorktreeKey].Path);
        Assert.Equal("charles", s.Registrar.Items[open.WorktreeKey].AgentId);
    }

    [Fact]
    public async Task Open_uses_the_default_repo_and_an_explicit_repo_wins()
    {
        using var s = new Setup();
        s.Repos.Set(new CodingRepoRecord(new RepoRef("other", "main"), Path.Combine(s.Fx.Base, "clone2"), 1, "o", "n2", 3));

        var a = await s.Workspace.OpenAsync("charles", "t1", cancellationToken: TestContext.Current.CancellationToken);
        var b = await s.Workspace.OpenAsync("charles", "t1", "other", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("repo", a.RepoId);
        Assert.Equal("other", b.RepoId);
        Assert.NotEqual(a.Path, b.Path);
    }

    [Fact]
    public async Task Keys_are_random_unique_and_unrelated_to_the_task()
    {
        using var s = new Setup();

        var a = await s.Workspace.OpenAsync("charles", "task-a", cancellationToken: TestContext.Current.CancellationToken);
        var b = await s.Workspace.OpenAsync("charles", "task-b", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(a.WorktreeKey, b.WorktreeKey);
        Assert.True(a.WorktreeKey.Length >= 43);
        Assert.DoesNotContain("task-a", a.WorktreeKey, StringComparison.Ordinal);
        Assert.All(a.WorktreeKey, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
    }

    [Fact]
    public async Task No_repo_and_no_default_is_refused()
    {
        using var s = new Setup(defaultRepo: null);

        Assert.Equal(CodingTaskWorkspaceError.NoRepo, await ErrorOf(s.Workspace.OpenAsync("charles", "t1")));
        Assert.Empty(s.Registrar.Items);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("../repo")]
    public async Task Unknown_repo_is_refused_and_nothing_is_created(string repo)
    {
        using var s = new Setup();

        var error = await ErrorOf(s.Workspace.OpenAsync("charles", "t1", repo));

        Assert.True(error is CodingTaskWorkspaceError.UnknownRepo or CodingTaskWorkspaceError.InvalidTask);
        Assert.Empty(s.Fx.Engine.WorktreePaths);
        Assert.Empty(s.Registrar.Items);
    }

    [Fact]
    public async Task Unknown_default_repo_is_refused()
    {
        using var s = new Setup("missing");

        Assert.Equal(CodingTaskWorkspaceError.UnknownRepo, await ErrorOf(s.Workspace.OpenAsync("charles", "t1")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("Task")]
    [InlineData("-x")]
    [InlineData("x.lock")]
    [InlineData("a..b")]
    [InlineData("nul")]
    [InlineData("con.txt")]
    [InlineData("trailing.")]
    [InlineData("has space")]
    [InlineData("c:evil")]
    public async Task Unsafe_task_id_is_refused(string task)
    {
        using var s = new Setup();

        Assert.Equal(CodingTaskWorkspaceError.InvalidTask, await ErrorOf(s.Workspace.OpenAsync("charles", task)));
        Assert.Empty(s.Fx.Engine.WorktreePaths);
        Assert.Empty(s.Registrar.Items);
    }

    [Fact]
    public async Task Too_long_task_id_is_refused()
    {
        using var s = new Setup();

        Assert.Equal(CodingTaskWorkspaceError.InvalidTask, await ErrorOf(s.Workspace.OpenAsync("charles", new string('a', 101))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("a/b")]
    public async Task Unsafe_agent_is_refused(string agent)
    {
        using var s = new Setup();

        Assert.Equal(CodingTaskWorkspaceError.InvalidAgent, await ErrorOf(s.Workspace.OpenAsync(agent, "t1")));
    }

    [Fact]
    public async Task A_second_open_of_the_same_task_is_refused()
    {
        using var s = new Setup();
        await s.Workspace.OpenAsync("charles", "t1", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(CodingTaskWorkspaceError.AlreadyExists, await ErrorOf(s.Workspace.OpenAsync("charles", "t1")));
        Assert.Single(s.Registrar.Items);
    }

    [Fact]
    public async Task An_existing_worktree_directory_is_refused_even_when_released()
    {
        using var s = new Setup();
        var first = await s.Workspace.OpenAsync("charles", "t1", cancellationToken: TestContext.Current.CancellationToken);
        Directory.CreateDirectory(first.Path);
        await s.Workspace.CloseAsync(first.WorktreeKey, TestContext.Current.CancellationToken);

        Assert.Equal(CodingTaskWorkspaceError.AlreadyExists, await ErrorOf(s.Workspace.OpenAsync("charles", "t1")));
    }

    [Fact]
    public async Task Close_unregisters_and_frees_the_task_for_a_later_open()
    {
        using var s = new Setup();
        var open = await s.Workspace.OpenAsync("charles", "t1", cancellationToken: TestContext.Current.CancellationToken);

        await s.Workspace.CloseAsync(open.WorktreeKey, TestContext.Current.CancellationToken);

        Assert.Empty(s.Registrar.Items);
        var again = await s.Workspace.OpenAsync("charles", "t1", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(open.WorktreeKey, again.WorktreeKey);
        await s.Workspace.CloseAsync("unknown-key", TestContext.Current.CancellationToken);
        await s.Workspace.CloseAsync(string.Empty, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Discard_removes_the_directory_and_unregisters()
    {
        using var s = new Setup();
        var open = await s.Workspace.OpenAsync("charles", "t1", cancellationToken: TestContext.Current.CancellationToken);
        Directory.CreateDirectory(open.Path);
        File.WriteAllText(Path.Combine(open.Path, "a.txt"), "x");

        await s.Workspace.DiscardAsync(open.WorktreeKey, TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(open.Path));
        Assert.Empty(s.Registrar.Items);
    }

    [Fact]
    public async Task Discard_never_follows_a_link_out_of_the_worktree()
    {
        using var s = new Setup();
        var open = await s.Workspace.OpenAsync("charles", "t1", cancellationToken: TestContext.Current.CancellationToken);
        Directory.CreateDirectory(open.Path);
        var outside = Path.Combine(s.Fx.Base, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(open.Path, "link"), outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        await s.Workspace.DiscardAsync(open.WorktreeKey, TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(open.Path));
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
    }

    [Fact]
    public async Task A_failing_registrar_leaves_nothing_held()
    {
        using var s = new Setup();
        s.Registrar.Fail = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Workspace.OpenAsync("charles", "t1"));

        s.Registrar.Fail = false;
        var open = await s.Workspace.OpenAsync("charles", "t1", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(s.Registrar.Items);
        Assert.NotNull(open);
    }

    [Fact]
    public async Task Another_agent_gets_its_own_share_for_the_same_task()
    {
        using var s = new Setup();

        var a = await s.Workspace.OpenAsync("charles", "t1", cancellationToken: TestContext.Current.CancellationToken);
        var b = await s.Workspace.OpenAsync("ada", "t2", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(a.Path, b.Path);
        Assert.True(WorkSharePaths.IsEqualOrUnder(b.Path, s.Fx.Source.Find("ada")!.WorktreeRoot));
    }

    private sealed class NoShares : IAgentWorkShareSource
    {
        public AgentWorkShare? Find(string agentId) => null;
    }

    [Fact]
    public async Task An_agent_with_no_work_share_is_refused()
    {
        using var s = new Setup();
        var workspace = new CodingTaskWorkspace(s.Fx.Git, s.Repos, new WorkShareLayout(new NoShares()), s.Registrar, "repo");

        var error = await ErrorOf(workspace.OpenAsync("charles", "t1"));

        Assert.Equal(CodingTaskWorkspaceError.InvalidAgent, error);
        Assert.Empty(s.Registrar.Items);
    }
}
