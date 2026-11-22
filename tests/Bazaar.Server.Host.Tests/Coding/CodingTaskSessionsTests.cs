using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingGit.Fakes;
using Bazaar.Server.Host.Coding;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.Host.Tests.Coding;

public sealed class CodingTaskSessionsTests : IDisposable
{
    private static readonly RepoRef _repo = new("repo", "main");
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-tasksessions-" + Guid.NewGuid().ToString("N"));
    private readonly CodingWorktreeRegistry _registry = new();
    private readonly CodingTaskSessions _sessions;

    public CodingTaskSessionsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "share"));
        Directory.CreateDirectory(Path.Combine(_root, "passport"));
        var git = new CodingGitService(
            new FixedWorkShareLayout(Path.Combine(_root, "share"), Path.Combine(_root, "passport")),
            new InMemoryCodingGitEngine(),
            new FakeHostPushPort(),
            new FakePushGate(),
            new FakeAgentGitIdentity(),
            new WorktreeLocks());
        _sessions = new CodingTaskSessions(git, _registry);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static CodingTaskStart Start(Guid session, string slug = "fix-login", string agent = "charles") =>
        new(session, agent, _repo, slug, "gc-1", "task-7");

    [Fact]
    public async Task Start_opens_the_task_branch_and_registers_it_under_the_session_id()
    {
        var session = Guid.NewGuid();

        var worktree = await _sessions.StartAsync(Start(session), _ct);

        Assert.Equal("agent/fix-login", worktree.Branch.Value);
        Assert.Same(worktree, _registry.Find(session.ToString("D")));
    }

    [Fact]
    public async Task A_second_start_for_the_same_session_is_refused()
    {
        var session = Guid.NewGuid();
        await _sessions.StartAsync(Start(session), _ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sessions.StartAsync(Start(session, "other-task"), _ct));
    }

    [Fact]
    public async Task Two_sessions_cannot_write_the_same_branch()
    {
        await _sessions.StartAsync(Start(Guid.NewGuid()), _ct);

        await Assert.ThrowsAsync<CodingGitException>(() => _sessions.StartAsync(Start(Guid.NewGuid(), agent: "dana"), _ct));
    }

    [Fact]
    public async Task End_releases_the_worktree_and_frees_the_branch_for_the_next_writer()
    {
        var first = Guid.NewGuid();
        var worktree = await _sessions.StartAsync(Start(first), _ct);

        Assert.True(await _sessions.EndAsync(first));

        Assert.False(worktree.IsActive);
        Assert.Null(_registry.Find(first.ToString("D")));
        Assert.False(await _sessions.EndAsync(first));
        await _sessions.StartAsync(Start(Guid.NewGuid()), _ct);
    }

    [Fact]
    public async Task The_session_answers_as_the_parent_work_source_while_it_holds_a_worktree()
    {
        var session = Guid.NewGuid();
        var worktree = await _sessions.StartAsync(Start(session), _ct);

        var work = await _sessions.FindAsync(session.ToString("D"), _ct);

        Assert.NotNull(work);
        Assert.Equal("charles", work.AgentId);
        Assert.Equal("fix-login", work.TaskSlug);
        Assert.Equal("gc-1", work.GcId);
        Assert.Equal(worktree.Path, work.WorktreePath);

        await _sessions.EndAsync(session);
        Assert.Null(await _sessions.FindAsync(session.ToString("D"), _ct));
        Assert.Null(await _sessions.FindAsync("unknown", _ct));
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Fact]
    public void The_repo_setting_is_null_when_unset_and_a_repo_when_both_values_are_set()
    {
        Assert.Null(CodingHarnessRepoOptions.From(Config()).Repo);
        Assert.Null(CodingHarnessRepoOptions.From(null).Repo);

        var set = CodingHarnessRepoOptions.From(Config(("Coding:Harness:Repo:Id", " bazaar "), ("Coding:Harness:Repo:DefaultBranch", "main")));
        Assert.Equal(new RepoRef("bazaar", "main"), set.Repo);
    }

    [Theory]
    [InlineData("bazaar", null)]
    [InlineData(null, "main")]
    [InlineData("bazaar", " ")]
    [InlineData("", "main")]
    public void A_half_set_or_blank_repo_setting_stops_the_host(string? id, string? branch)
    {
        Assert.Throws<InvalidOperationException>(() => CodingHarnessRepoOptions.From(Config(("Coding:Harness:Repo:Id", id), ("Coding:Harness:Repo:DefaultBranch", branch))));
    }
}
