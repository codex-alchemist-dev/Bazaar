using Bazaar.Server.Agents.Turns;
using Bazaar.Server.CodingEdit;
using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingGit.Fakes;
using Bazaar.Server.CodingRunner;
using Bazaar.Server.CodingRunner.Handlers;
using Bazaar.Server.Host.Coding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests.Coding;

public sealed class RunningTurnCodingToolHostContextTests : IDisposable
{
    private static readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private readonly string _base = Path.Combine(Path.GetTempPath(), "bazaar-ctx-" + Guid.NewGuid().ToString("N"));
    private readonly InProcessRunningTurnRegistry _registry = new();
    private readonly CodingWorktreeRegistry _worktrees = new();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly Guid _session = Guid.NewGuid();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private RunningTurnCodingToolHostContext Context(CodingAgentModel? model = null)
    {
        var map = new Dictionary<string, CodingAgentModel>();
        if (model is not null)
        {
            map[_agent.ToString()] = model;
        }

        return new RunningTurnCodingToolHostContext(_registry, _worktrees, new ConfiguredCodingAgentModels(map));
    }

    private static CodingAgentModel Model() =>
        new("claude-sonnet-5-5", "Claude Sonnet 5.5", "credit@example.test", new HashSet<string> { "claude-haiku-5-5", "claude-sonnet-5-5" });

    private IDisposable Turn(Guid? turnId = null, Guid? session = null, int depth = 0, string[]? tools = null) =>
        _registry.Register(new RunningTurnInfo(turnId ?? Guid.NewGuid(), _agent, session ?? _session, "holder", tools ?? ["read", "spawn"], depth, 10, "gc-1", 3, ""));

    private CodingToolCall Call(string? agent = null, string? session = null) =>
        new(agent ?? _agent.ToString(), session ?? _session.ToString(), "spawn");

    private async Task<CodingWorktree> OpenWorktreeAsync()
    {
        var share = Path.Combine(_base, "share");
        Directory.CreateDirectory(share);
        var service = new CodingGitService(
            new FixedWorkShareLayout(share, Path.Combine(_base, "passport")),
            new InMemoryCodingGitEngine(),
            new FakeHostPushPort(),
            new FakePushGate(),
            new FakeAgentGitIdentity(),
            new WorktreeLocks());
        return await service.CreateWorktreeAsync(new CreateWorktreeRequest("charles", new RepoRef("repo", "main"), "t1", "gc-1", "task-1"), _ct);
    }

    [Fact]
    public void The_parent_comes_from_the_one_running_turn_of_the_calling_session()
    {
        using var _ = Turn(depth: 1);

        var parent = Context(Model()).ParentFor(Call());

        Assert.NotNull(parent);
        Assert.Equal((_session.ToString(), _session.ToString(), 1), (parent.ParentId, parent.ChainId, parent.Depth));
        Assert.Equal(["read", "spawn"], parent.ToolAllowlist.Order());
        Assert.Equal(["claude-haiku-5-5", "claude-sonnet-5-5"], parent.ModelWhitelist.Order());
    }

    [Fact]
    public void No_running_turn_gives_no_parent_and_no_model_facts()
    {
        var context = Context(Model());

        Assert.Null(context.ParentFor(Call()));
        Assert.Throws<InvalidOperationException>(() => context.ModelIdFor(_agent.ToString()));
        Assert.Throws<InvalidOperationException>(() => context.CommitModelFor(_agent.ToString()));
    }

    [Fact]
    public void A_turn_that_has_ended_gives_no_context()
    {
        Turn().Dispose();

        Assert.Null(Context(Model()).ParentFor(Call()));
    }

    [Fact]
    public void A_session_that_is_not_the_calling_one_or_ids_that_are_not_guids_give_no_parent()
    {
        using var _ = Turn();
        var context = Context(Model());

        Assert.Null(context.ParentFor(Call(session: Guid.NewGuid().ToString())));
        Assert.Null(context.ParentFor(Call(agent: Guid.NewGuid().ToString())));
        Assert.Null(context.ParentFor(Call(agent: "charles")));
        Assert.Null(context.ParentFor(Call(session: "session-1")));
        Assert.Null(context.ParentFor(Call(session: "")));
        Assert.Throws<InvalidOperationException>(() => context.ModelIdFor("charles"));
    }

    [Fact]
    public void A_parent_and_its_subagent_in_one_session_are_ambiguous_so_the_parent_is_refused()
    {
        using var a = Turn(depth: 0);
        using var b = Turn(depth: 1);

        Assert.Null(Context(Model()).ParentFor(Call()));
    }

    [Fact]
    public void A_turn_in_another_session_of_the_same_agent_does_not_make_the_call_ambiguous()
    {
        using var a = Turn();
        using var b = Turn(session: Guid.NewGuid());

        Assert.NotNull(Context(Model()).ParentFor(Call()));
    }

    [Fact]
    public void An_agent_with_no_configured_model_is_refused_everywhere()
    {
        using var _ = Turn();
        var context = Context(model: null);

        Assert.Null(context.ParentFor(Call()));
        Assert.Throws<InvalidOperationException>(() => context.ModelIdFor(_agent.ToString()));
        Assert.Throws<InvalidOperationException>(() => context.CommitModelFor(_agent.ToString()));
    }

    [Fact]
    public void Model_facts_come_from_the_agents_configured_model_while_it_runs()
    {
        using var _ = Turn();
        var context = Context(Model());

        Assert.Equal("claude-sonnet-5-5", context.ModelIdFor(_agent.ToString()));
        Assert.Equal(new ModelIdentity("Claude Sonnet 5.5", "credit@example.test"), context.CommitModelFor(_agent.ToString()));
    }

    [Fact]
    public async Task Only_a_registered_active_worktree_is_found()
    {
        var worktree = await OpenWorktreeAsync();
        var context = Context(Model());

        Assert.Null(context.FindWorktree("key-1"));
        Assert.Null(context.FindWorktree(""));
        Assert.Null(context.FindWorktree("  "));
        var registration = _worktrees.Register("key-1", worktree);
        Assert.Same(worktree, context.FindWorktree("key-1"));
        Assert.Null(context.FindWorktree("KEY-1"));
        Assert.Throws<InvalidOperationException>(() => _worktrees.Register("key-1", worktree));

        await worktree.DisposeAsync();
        Assert.Null(context.FindWorktree("key-1"));

        registration.Dispose();
        registration.Dispose();
        Assert.Null(_worktrees.Find("key-1"));
    }

    [Fact]
    public void Configured_models_are_read_and_a_bad_entry_stops_the_start_naming_the_key()
    {
        var good = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Coding:Harness:Models:a1:ModelId"] = "m1",
            ["Coding:Harness:Models:a1:Name"] = "Model One",
            ["Coding:Harness:Models:a1:Email"] = "credit@example.test",
            ["Coding:Harness:Models:a1:Whitelist:0"] = "m1",
            ["Coding:Harness:Models:a1:Whitelist:1"] = "m2",
        }).Build();

        var found = ConfiguredCodingAgentModels.FromConfiguration(good).Find("a1");

        Assert.Equal(("m1", "Model One", "credit@example.test"), (found!.ModelId, found.CreditName, found.CreditEmail));
        Assert.Equal(["m1", "m2"], found.ModelWhitelist.Order());
        Assert.Null(ConfiguredCodingAgentModels.FromConfiguration(good).Find("a2"));

        foreach (var (field, value) in new[] { ("ModelId", ""), ("Name", "Bad <Name>"), ("Email", "no-at-sign"), ("Email", "a b@example.test") })
        {
            var bad = new Dictionary<string, string?>
            {
                ["Coding:Harness:Models:a1:ModelId"] = "m1",
                ["Coding:Harness:Models:a1:Name"] = "Model One",
                ["Coding:Harness:Models:a1:Email"] = "credit@example.test",
            };
            bad[$"Coding:Harness:Models:a1:{field}"] = value;

            var error = Assert.Throws<InvalidOperationException>(() =>
                ConfiguredCodingAgentModels.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(bad).Build()));
            Assert.Contains($"Coding:Harness:Models:a1:{field}", error.Message);
        }
    }

    [Fact]
    public void The_harness_registration_binds_both_ports_and_a_host_binding_made_first_wins()
    {
        var services = new ServiceCollection();
        services.AddRunningTurnParentDirectory();
        services.AddSingleton<IWorkShareLayout>(new FixedWorkShareLayout(Path.Combine(_base, "s"), Path.Combine(_base, "p")));
        services.AddBazaarCodingHarnessAgentPorts(CodingHarnessTestFakes.Config(true));
        using var provider = services.BuildServiceProvider();

        Assert.IsType<WorktreeFilesSource>(provider.GetRequiredService<IWorktreeFilesSource>());
        Assert.IsType<RunningTurnCodingToolHostContext>(provider.GetRequiredService<ICodingToolHostContext>());
        Assert.Same(provider.GetRequiredService<CodingWorktreeRegistry>(), provider.GetRequiredService<ICodingWorktreeDirectory>());

        var mine = new NoFiles();
        var own = new ServiceCollection();
        own.AddSingleton<IWorktreeFilesSource>(mine);
        own.AddBazaarCodingHarnessAgentPorts(CodingHarnessTestFakes.Config(true));
        using var ownProvider = own.BuildServiceProvider();

        Assert.Same(mine, ownProvider.GetRequiredService<IWorktreeFilesSource>());
    }

    private sealed class NoFiles : IWorktreeFilesSource
    {
        public IWorktreeFiles? FilesFor(CodingWorktree worktree) => null;
    }
}
