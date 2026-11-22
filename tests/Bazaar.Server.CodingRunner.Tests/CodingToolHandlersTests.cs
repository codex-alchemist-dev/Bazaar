using System.Text.Json;
using Bazaar.Contracts.Coding;
using Bazaar.Server.CodingEdit;
using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingGit.Fakes;
using Bazaar.Server.CodingPermissions;
using Bazaar.Server.CodingReview;
using Bazaar.Server.CodingReview.InMemory;
using Bazaar.Server.CodingRunner.Handlers;
using Bazaar.Server.RepoMap;
using Bazaar.Server.Subagents;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.CodingRunner.Tests;

/// <summary>Tests for the seven coding handlers (doc 17, WP15 follow-up), run through the real runner rig where the gate matters.</summary>
public sealed class CodingToolHandlersTests
{
    private const string _wt = "wt-1";

    // ---- fakes -------------------------------------------------------------------------------------------------------

    private sealed class FakeSearch(Func<SearchRequest, Task<SearchResult>> run) : ISearchTool
    {
        public SearchRequest? Last { get; private set; }

        public Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken ct)
        {
            Last = request;
            return run(request);
        }
    }

    private sealed class FakeRepoMap(Func<RepoMapRequest, Task<RepoMapResult>> run) : IRepoMapBuilder
    {
        public RepoMapRequest? Last { get; private set; }

        public Task<RepoMapResult> BuildAsync(RepoMapRequest request, CancellationToken ct)
        {
            Last = request;
            return run(request);
        }
    }

    private sealed class FakeFilesSource : IWorktreeFilesSource
    {
        public InMemoryWorktreeFiles Files { get; } = new();

        /// <summary>False simulates a host with no file store bound for the worktree.</summary>
        public bool Available { get; set; } = true;

        public int Requests { get; private set; }

        public IWorktreeFiles? FilesFor(CodingWorktree worktree)
        {
            Requests++;
            return Available ? Files : null;
        }
    }

    private sealed class FakeSpawner(Func<SubagentSpec, Task<SubagentHandle>> run) : ISubagentSpawner
    {
        public SubagentSpec? Last { get; private set; }

        public Task<SubagentHandle> SpawnAsync(ParentContext parent, SubagentSpec spec, CancellationToken cancellationToken)
        {
            Last = spec;
            return run(spec);
        }
    }

    private sealed class FakeReviewer(bool approve) : IReviewerSession
    {
        public Task<ReviewerSessionResult> RunAsync(ReviewerSessionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ReviewerSessionResult(approve, "reviewed"));
    }

    private sealed class FakeHost : ICodingToolHostContext
    {
        public Dictionary<string, CodingWorktree> Worktrees { get; } = new(StringComparer.Ordinal);

        public ParentContext? Parent { get; set; }

        public int FindCalls { get; private set; }

        public CodingWorktree? FindWorktree(string worktreeKey)
        {
            FindCalls++;
            return Worktrees.GetValueOrDefault(worktreeKey);
        }

        public ModelIdentity CommitModelFor(string agentId) => new("Claude Haiku 5.5", "noreply@anthropic.com");

        public string ModelIdFor(string agentId) => "claude-author";

        public ParentContext? ParentFor(CodingToolCall toolCall) => Parent;
    }

    // ---- harness -----------------------------------------------------------------------------------------------------

    private sealed class Harness : IDisposable
    {
        public Harness(Func<SearchRequest, Task<SearchResult>>? search = null, Func<RepoMapRequest, Task<RepoMapResult>>? map = null, Func<SubagentSpec, Task<SubagentHandle>>? spawn = null, bool reviewApproves = true, bool reviewerPool = true)
        {
            Base = Path.Combine(Path.GetTempPath(), "bazaar-handlers-" + Guid.NewGuid().ToString("N"));
            Share = Path.Combine(Base, "share");
            Passport = Path.Combine(Base, "passport");
            Directory.CreateDirectory(Share);
            Directory.CreateDirectory(Passport);

            Search = new FakeSearch(search ?? (_ => Task.FromResult(new SearchResult([new SearchMatch("src/a.cs", 3, "class A")], false))));
            RepoMap = new FakeRepoMap(map ?? (_ => Task.FromResult(new RepoMapResult("src/a.cs\n  3: class A", 1, 0, 1, 0, 9))));
            Spawner = new FakeSpawner(spawn ?? (_ => Task.FromResult(CompletedHandle())));

            Engine = new InMemoryCodingGitEngine();
            Port = new FakeHostPushPort();
            PushGate = new FakePushGate();
            Git = new CodingGitService(new FixedWorkShareLayout(Share, Passport), Engine, Port, PushGate, new FakeAgentGitIdentity(), new WorktreeLocks());

            Sources = new InMemoryReviewContextSources();
            Models = new InMemoryReviewModelDirectory();
            if (reviewerPool)
            {
                Models.Add(new ReviewerPool("claude-author", "anthropic", [new ReviewModelOption("gpt-review", "openai")]));
            }

            Sink = new InMemoryReviewResultSink();
            Review = new ReviewService(Sources, Models, new FakeReviewer(reviewApproves), Sink, new ReviewContextBuilder(ReviewContextLimits.Default));
            Host = new FakeHost { Parent = new ParentContext("session-1", "chain-1", new HashSet<string>(["search", "read"], StringComparer.Ordinal), new HashSet<string>(["claude-child"], StringComparer.Ordinal), 0) };
        }

        public string Base { get; }

        public string Share { get; }

        public string Passport { get; }

        public FakeSearch Search { get; }

        public FakeRepoMap RepoMap { get; }

        public FakeSpawner Spawner { get; }

        public FakeFilesSource FilesSource { get; } = new();

        public InMemoryCodingGitEngine Engine { get; }

        public FakeHostPushPort Port { get; }

        public FakePushGate PushGate { get; }

        public CodingGitService Git { get; }

        public InMemoryReviewContextSources Sources { get; }

        public InMemoryReviewModelDirectory Models { get; }

        public InMemoryReviewResultSink Sink { get; }

        public ReviewService Review { get; }

        public FakeHost Host { get; }

        public static SubagentHandle CompletedHandle(SubagentStatus status = SubagentStatus.Completed) =>
            new("sub-1", "chain-1", 1, Task.FromResult(new SubagentResult(status, new Bazaar.Server.Subagents.UntrustedText("done"), new SubagentUsage(10, 5, 2), "trace-9")), () => { });

        /// <summary>Opens a worktree under the work share and registers it with the host as <see cref="_wt"/>.</summary>
        public async Task<CodingWorktree> OpenWorktreeAsync()
        {
            var worktree = await Git.CreateWorktreeAsync(new CreateWorktreeRequest("agent-1", new RepoRef("repo", "main"), "fix-login", "gc-1", "task-7"));
            Host.Worktrees[_wt] = worktree;
            return worktree;
        }

        /// <summary>Every handler, built on this harness.</summary>
        public Dictionary<string, ICodingToolHandler> Handlers() => new(StringComparer.Ordinal)
        {
            [ReadToolHandler.ToolName] = new ReadToolHandler(FilesSource, Host),
            [ListToolHandler.ToolName] = new ListToolHandler(FilesSource, Host),
            [SearchToolHandler.ToolName] = new SearchToolHandler(Search),
            [RepoMapToolHandler.ToolName] = new RepoMapToolHandler(RepoMap),
            [GitDiffToolHandler.ToolName] = new GitDiffToolHandler(Git, Host),
            [GitCommitToolHandler.ToolName] = new GitCommitToolHandler(Git, Host),
            [GitPushDraftPrToolHandler.ToolName] = new GitPushDraftPrToolHandler(Git, Host),
            [ReviewToolHandler.ToolName] = new ReviewToolHandler(Review, Host),
            [SubagentSpawnToolHandler.ToolName] = new SubagentSpawnToolHandler(Spawner, Host),
        };

        /// <summary>A runner over the real gate and recorder, with the given policy (allow by default).</summary>
        public CodingToolRunner Runner(Func<ToolCallContext, GenericPolicyDecision>? policy = null)
        {
            var trace = new InMemoryToolCallTrace();
            Trace = trace;
            var gate = new ToolPermissionGate(
                new FakeGenericToolPolicy(c => (policy ?? (_ => new GenericPolicyDecision(PermissionOutcome.Allow, "ok")))(c)),
                trace,
                new InMemoryAgentFreeze(),
                new CodingPermissionOptions());
            return new CodingToolRunner(Handlers().Values, new ToolCallOutcomeRecorder(gate, trace), new WorktreeLocks(), null, null);
        }

        public InMemoryToolCallTrace? Trace { get; private set; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Base, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------

    private static CodingToolCall Call(string tool, string json = "{}", string? worktree = null) =>
        new("agent-1", "session-1", tool, JsonElement.Parse(json), Role: ToolSessionRole.Actor, Taint: SessionTaint.None, WorktreeKey: worktree);

    private static JsonElement Root(string output) => JsonElement.Parse(output);

    private static string ErrorCode(CodingToolResult result) => Root(result.Output).GetProperty("error").GetProperty("code").GetString()!;

    private static readonly (string Tool, string Capability, bool Writes)[] _declared =
    [
        (ReadToolHandler.ToolName, "Read", false),
        (ListToolHandler.ToolName, "Read", false),
        (SearchToolHandler.ToolName, "Read", false),
        (RepoMapToolHandler.ToolName, "Read", false),
        (GitDiffToolHandler.ToolName, "Read", false),
        (GitCommitToolHandler.ToolName, "Exec", true),
        (GitPushDraftPrToolHandler.ToolName, "Post", false),
        (ReviewToolHandler.ToolName, "Post", false),
        (SubagentSpawnToolHandler.ToolName, "Exec", false),
    ];

    // ---- registration and declared classes ---------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(DeclaredRows))]
    public void Handler_declares_its_capability_class_and_worktree_flag(string tool, string capability, bool writes)
    {
        using var h = new Harness();
        var handler = h.Handlers()[tool];

        Assert.Equal(tool, handler.Name);
        Assert.Equal(capability, handler.CapabilityClass.ToString());
        Assert.Equal(writes, handler.WritesWorktree);
    }

    public static TheoryData<string, string, bool> DeclaredRows()
    {
        var data = new TheoryData<string, string, bool>();
        foreach (var row in _declared)
        {
            data.Add(row.Tool, row.Capability, row.Writes);
        }

        return data;
    }

    [Fact]
    public void Registration_adds_one_handler_per_tool_with_unique_names()
    {
        using var h = new Harness();
        var services = new ServiceCollection();
        services.AddSingleton<ISearchTool>(h.Search);
        services.AddSingleton<IRepoMapBuilder>(h.RepoMap);
        services.AddSingleton(h.Git);
        services.AddSingleton(h.Review);
        services.AddSingleton<ISubagentSpawner>(h.Spawner);
        services.AddSingleton<ICodingToolHostContext>(h.Host);
        services.AddSingleton<IWorktreeFilesSource>(h.FilesSource);
        services.AddBazaarCodingToolHandlers();

        using var provider = services.BuildServiceProvider();
        var names = provider.GetServices<ICodingToolHandler>().Select(x => x.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(_declared.Select(d => d.Tool).Order(StringComparer.Ordinal).ToArray(), names);
    }

    [Fact]
    public void Registration_without_the_host_context_fails_resolution()
    {
        using var h = new Harness();
        var services = new ServiceCollection();
        services.AddSingleton<ISearchTool>(h.Search);
        services.AddSingleton<IRepoMapBuilder>(h.RepoMap);
        services.AddSingleton(h.Git);
        services.AddSingleton(h.Review);
        services.AddSingleton<ISubagentSpawner>(h.Spawner);
        services.AddSingleton<IWorktreeFilesSource>(h.FilesSource);
        services.AddBazaarCodingToolHandlers();

        using var provider = services.BuildServiceProvider();

        Assert.ThrowsAny<Exception>(() => provider.GetServices<ICodingToolHandler>().ToArray());
    }

    // ---- gate: every handler refused by the gate never runs ----------------------------------------------------------

    [Fact]
    public async Task Every_handler_is_refused_by_a_denying_gate_and_never_runs()
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        var runner = h.Runner(_ => new GenericPolicyDecision(PermissionOutcome.Deny, "nope"));
        var samples = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ReadToolHandler.ToolName] = """{"path":"src/a.cs"}""",
            [ListToolHandler.ToolName] = "{}",
            [SearchToolHandler.ToolName] = """{"pattern":"x"}""",
            [RepoMapToolHandler.ToolName] = "{}",
            [GitDiffToolHandler.ToolName] = "{}",
            [GitCommitToolHandler.ToolName] = """{"subject":"Fix"}""",
            [GitPushDraftPrToolHandler.ToolName] = """{"title":"Fix"}""",
            [ReviewToolHandler.ToolName] = """{"repository":"repo","number":1,"base_commit":"abcdef1","head_commit":"abcdef2"}""",
            [SubagentSpawnToolHandler.ToolName] = """{"task":"t","tool_allowlist":["search"],"model_id":"claude-child"}""",
        };

        foreach (var (tool, json) in samples)
        {
            var result = await runner.RunAsync(Call(tool, json, _wt), CancellationToken.None);

            Assert.Equal(CodingToolStatus.Denied, result.Status);
            Assert.Equal("denied", result.ErrorCode);
        }

        Assert.Null(h.Search.Last);
        Assert.Null(h.RepoMap.Last);
        Assert.Null(h.Spawner.Last);
        Assert.Empty(h.Engine.Commits);
        Assert.Empty(h.Port.Requests);
        Assert.Empty(h.Sink.Posted);
        Assert.Equal(0, h.Host.FindCalls);
        Assert.Equal(0, h.FilesSource.Requests);
    }

    [Theory]
    [MemberData(nameof(DeclaredRows))]
    public async Task Each_handler_trace_row_carries_its_declared_capability_class(string tool, string capability, bool writes)
    {
        using var h = new Harness();
        await using var worktree = writes || tool == GitDiffToolHandler.ToolName || tool == GitPushDraftPrToolHandler.ToolName ? await h.OpenWorktreeAsync() : null;
        var runner = h.Runner();
        var json = tool switch
        {
            SearchToolHandler.ToolName => """{"pattern":"x"}""",
            GitCommitToolHandler.ToolName => """{"subject":"Fix"}""",
            GitPushDraftPrToolHandler.ToolName => """{"title":"Fix"}""",
            ReviewToolHandler.ToolName => """{"repository":"repo","number":1,"base_commit":"abcdef1","head_commit":"abcdef2"}""",
            SubagentSpawnToolHandler.ToolName => """{"task":"t","tool_allowlist":["search"],"model_id":"claude-child"}""",
            _ => "{}",
        };

        await runner.RunAsync(Call(tool, json, _wt), CancellationToken.None);

        Assert.Equal(capability, Assert.Single(h.Trace!.Rows).CapabilityClass);
    }

    [Fact]
    public async Task Writer_handler_without_a_worktree_key_is_refused_by_the_runner_and_is_traced()
    {
        using var h = new Harness();
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(GitCommitToolHandler.ToolName, """{"subject":"Fix"}"""), CancellationToken.None);

        Assert.Equal("worktree_required", result.ErrorCode);
        Assert.Equal(64, result.ExitCode);
        Assert.Empty(h.Engine.Commits);
    }

    [Fact]
    public async Task Read_handler_without_a_worktree_key_gets_a_structured_no_worktree_error()
    {
        using var h = new Harness();
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(GitDiffToolHandler.ToolName), CancellationToken.None);

        Assert.Equal(CodingToolStatus.Failed, result.Status);
        Assert.Equal("no_worktree", ErrorCode(result));
        Assert.Equal(64, result.ExitCode);
    }

    // ---- malformed arguments are structured, never exceptions --------------------------------------------------------

    [Theory]
    [InlineData(ReadToolHandler.ToolName)]
    [InlineData(ListToolHandler.ToolName)]
    [InlineData(SearchToolHandler.ToolName)]
    [InlineData(RepoMapToolHandler.ToolName)]
    [InlineData(GitDiffToolHandler.ToolName)]
    [InlineData(GitCommitToolHandler.ToolName)]
    [InlineData(GitPushDraftPrToolHandler.ToolName)]
    [InlineData(ReviewToolHandler.ToolName)]
    [InlineData(SubagentSpawnToolHandler.ToolName)]
    public async Task Unknown_argument_field_is_rejected_as_malformed_for_every_handler(string tool)
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(tool, """{"surprise":"value-the-model-sent"}""", _wt), CancellationToken.None);

        Assert.Equal(CodingToolStatus.Failed, result.Status);
        Assert.Equal("malformed_args", ErrorCode(result));
        Assert.Equal(64, result.ExitCode);
        Assert.DoesNotContain("value-the-model-sent", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"pattern":""}""")]
    [InlineData("""{"pattern":5}""")]
    [InlineData("""{"pattern":"x","max_results":0}""")]
    [InlineData("""{"pattern":"x","max_results":"many"}""")]
    [InlineData("""{"pattern":"x","case_insensitive":"yes"}""")]
    public async Task Search_rejects_malformed_arguments(string json)
    {
        using var h = new Harness();
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(SearchToolHandler.ToolName, json), CancellationToken.None);

        Assert.Equal("malformed_args", ErrorCode(result));
        Assert.Null(h.Search.Last);
    }

    [Fact]
    public async Task Search_valid_call_returns_matches_as_data()
    {
        using var h = new Harness();
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(SearchToolHandler.ToolName, """{"pattern":"class","glob":"*.cs","max_results":5,"case_insensitive":true}"""), CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("class", h.Search.Last!.Pattern);
        Assert.Equal("*.cs", h.Search.Last.Glob);
        Assert.Equal(5, h.Search.Last.MaxResults);
        Assert.True(h.Search.Last.CaseInsensitive);
        var match = Root(result.Output).GetProperty("matches")[0];
        Assert.Equal("src/a.cs", match.GetProperty("path").GetString());
        Assert.Equal(3, match.GetProperty("line").GetInt32());
    }

    [Fact]
    public async Task Search_defaults_apply_when_optional_arguments_are_absent()
    {
        using var h = new Harness();
        var runner = h.Runner();

        await runner.RunAsync(Call(SearchToolHandler.ToolName, """{"pattern":"x"}"""), CancellationToken.None);

        Assert.Equal(100, h.Search.Last!.MaxResults);
        Assert.False(h.Search.Last.CaseInsensitive);
        Assert.Null(h.Search.Last.Glob);
    }

    [Fact]
    public async Task Search_tool_argument_error_is_reported_without_echoing_the_input()
    {
        using var h = new Harness(search: _ => throw new ArgumentException("bad pattern: secret-input"));
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(SearchToolHandler.ToolName, """{"pattern":"x"}"""), CancellationToken.None);

        Assert.Equal("malformed_args", ErrorCode(result));
        Assert.DoesNotContain("secret-input", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_failure_is_a_failed_result_never_a_partial_success()
    {
        using var h = new Harness(search: _ => throw new RepoToolException("ripgrep timed out"));
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(SearchToolHandler.ToolName, """{"pattern":"x"}"""), CancellationToken.None);

        Assert.Equal(CodingToolStatus.Failed, result.Status);
        Assert.Equal("search_failed", ErrorCode(result));
        Assert.DoesNotContain("ripgrep timed out", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repo_map_valid_call_passes_defaults_and_focus_files()
    {
        using var h = new Harness();
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(RepoMapToolHandler.ToolName, """{"root":"src","token_budget":2000,"focus_files":["src/a.cs"]}"""), CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal("src", h.RepoMap.Last!.Root);
        Assert.Equal(2000, h.RepoMap.Last.TokenBudget);
        var focus = h.RepoMap.Last.FocusFiles!.ToArray();
        Assert.Equal("src/a.cs", Assert.Single(focus));
        Assert.False(Root(result.Output).GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Repo_map_defaults_to_the_whole_worktree_with_a_1000_token_budget()
    {
        using var h = new Harness();
        var runner = h.Runner();

        await runner.RunAsync(Call(RepoMapToolHandler.ToolName), CancellationToken.None);

        Assert.Equal(".", h.RepoMap.Last!.Root);
        Assert.Equal(1000, h.RepoMap.Last.TokenBudget);
        Assert.Null(h.RepoMap.Last.FocusFiles);
    }

    [Theory]
    [InlineData("""{"token_budget":0}""")]
    [InlineData("""{"token_budget":16001}""")]
    [InlineData("""{"token_budget":"big"}""")]
    [InlineData("""{"focus_files":"src/a.cs"}""")]
    [InlineData("""{"focus_files":[""]}""")]
    public async Task Repo_map_rejects_malformed_arguments(string json)
    {
        using var h = new Harness();
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(RepoMapToolHandler.ToolName, json), CancellationToken.None);

        Assert.Equal("malformed_args", ErrorCode(result));
        Assert.Null(h.RepoMap.Last);
    }

    [Fact]
    public async Task Repo_map_failure_is_structured()
    {
        using var h = new Harness(map: _ => throw new RepoToolException("listing failed at /secret/host/path"));
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(RepoMapToolHandler.ToolName), CancellationToken.None);

        Assert.Equal("repo_map_failed", ErrorCode(result));
        Assert.DoesNotContain("/secret/host/path", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Git_diff_returns_the_patch_for_the_worktree()
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        h.Engine.StageChange(worktree.Path, "src/a.cs", "+line");
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(GitDiffToolHandler.ToolName, "{}", _wt), CancellationToken.None);

        Assert.True(result.IsOk);
        var root = Root(result.Output);
        Assert.Equal("main", root.GetProperty("base_branch").GetString());
        Assert.False(root.GetProperty("patch_truncated").GetBoolean());
    }

    [Fact]
    public async Task Git_diff_rejects_arguments_it_does_not_take()
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(GitDiffToolHandler.ToolName, """{"base":"other"}""", _wt), CancellationToken.None);

        Assert.Equal("malformed_args", ErrorCode(result));
    }

    [Fact]
    public async Task Git_diff_key_the_host_does_not_hold_is_no_worktree()
    {
        using var h = new Harness();
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(GitDiffToolHandler.ToolName, "{}", "wt-unknown"), CancellationToken.None);

        Assert.Equal("no_worktree", ErrorCode(result));
    }

    [Fact]
    public async Task Git_commit_commits_staged_changes_as_the_host_model()
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        h.Engine.StageChange(worktree.Path, "src/a.cs", "+line");
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(GitCommitToolHandler.ToolName, """{"subject":"Fix login","body":"Details."}""", _wt), CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.False(string.IsNullOrEmpty(Root(result.Output).GetProperty("sha").GetString()));
        var commit = Assert.Single(h.Engine.Commits);
        Assert.Contains("Claude Haiku 5.5", commit.Trailers.CoAuthoredBy, StringComparison.Ordinal);
        Assert.Equal("agent-1", commit.Trailers.AgentId);
    }

    [Theory]
    [InlineData("""{"body":"no subject"}""")]
    [InlineData("""{"subject":""}""")]
    [InlineData("""{"subject":42}""")]
    public async Task Git_commit_rejects_malformed_arguments(string json)
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(GitCommitToolHandler.ToolName, json, _wt), CancellationToken.None);

        Assert.Equal("malformed_args", ErrorCode(result));
        Assert.Empty(h.Engine.Commits);
    }

    [Fact]
    public async Task Git_commit_with_nothing_staged_is_a_structured_git_error()
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(GitCommitToolHandler.ToolName, """{"subject":"Fix"}""", _wt), CancellationToken.None);

        Assert.Equal(CodingToolStatus.Failed, result.Status);
        Assert.Equal("git_nothing_to_commit", ErrorCode(result));
    }

    [Fact]
    public async Task Git_commit_refuses_a_signed_off_by_trailer_in_the_body()
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        h.Engine.StageChange(worktree.Path, "src/a.cs", "+line");
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(GitCommitToolHandler.ToolName, """{"subject":"Fix","body":"Signed-off-by: Someone <a@b.c>"}""", _wt), CancellationToken.None);

        Assert.Equal("git_forbidden_trailer", ErrorCode(result));
        Assert.Empty(h.Engine.Commits);
    }

    [Fact]
    public async Task Git_push_opens_a_draft_pr_when_the_gate_allows()
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(GitPushDraftPrToolHandler.ToolName, """{"title":"Fix login","body":"Why."}""", _wt), CancellationToken.None);

        Assert.True(result.IsOk);
        var root = Root(result.Output);
        Assert.Equal(1, root.GetProperty("number").GetInt32());
        Assert.True(root.GetProperty("is_draft").GetBoolean());
        Assert.Single(h.Port.Requests);
    }

    [Fact]
    public async Task Git_push_gate_needing_approval_pushes_nothing()
    {
        using var h = new Harness();
        h.PushGate.Decision = PushDecision.NeedsApproval("per-push approval required");
        await using var worktree = await h.OpenWorktreeAsync();
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(GitPushDraftPrToolHandler.ToolName, """{"title":"Fix login"}""", _wt), CancellationToken.None);

        Assert.Equal(CodingToolStatus.Failed, result.Status);
        Assert.Equal("git_push_needs_approval", ErrorCode(result));
        Assert.Empty(h.Port.Requests);
    }

    [Fact]
    public async Task Git_push_gate_denial_pushes_nothing()
    {
        using var h = new Harness();
        h.PushGate.Decision = PushDecision.Deny("denied");
        await using var worktree = await h.OpenWorktreeAsync();
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(GitPushDraftPrToolHandler.ToolName, """{"title":"Fix login"}""", _wt), CancellationToken.None);

        Assert.Equal("git_push_denied", ErrorCode(result));
        Assert.Empty(h.Port.Requests);
    }

    [Fact]
    public async Task Git_push_refuses_a_branch_other_than_the_worktree_branch()
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(GitPushDraftPrToolHandler.ToolName, """{"title":"Fix","branch":"main"}""", _wt), CancellationToken.None);

        Assert.Equal(CodingToolStatus.Failed, result.Status);
        Assert.StartsWith("git_", ErrorCode(result), StringComparison.Ordinal);
        Assert.Empty(h.Port.Requests);
    }

    [Fact]
    public async Task Git_push_rejects_a_missing_title()
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(GitPushDraftPrToolHandler.ToolName, """{"body":"x"}""", _wt), CancellationToken.None);

        Assert.Equal("malformed_args", ErrorCode(result));
        Assert.Empty(h.Port.Requests);
    }

    [Fact]
    public async Task Review_approves_and_sets_the_flag_with_the_host_author_model()
    {
        using var h = new Harness(reviewApproves: true);
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(ReviewToolHandler.ToolName, """{"repository":"repo","number":12,"base_commit":"abcdef1","head_commit":"1234567"}"""), CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal("approved", Root(result.Output).GetProperty("verdict").GetString());
        Assert.Equal("gpt-review", Root(result.Output).GetProperty("reviewer_model_id").GetString());
        Assert.Single(h.Sink.Posted);
        Assert.True(Assert.Single(h.Sink.Flags).Approved);
    }

    [Fact]
    public async Task Review_not_approved_is_a_completed_review_with_a_verdict()
    {
        using var h = new Harness(reviewApproves: false);
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(ReviewToolHandler.ToolName, """{"repository":"repo","number":12,"base_commit":"abcdef1","head_commit":"1234567"}"""), CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal("not_approved", Root(result.Output).GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task Review_without_a_reviewer_pool_is_a_failure_and_not_approved()
    {
        using var h = new Harness(reviewerPool: false);
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(ReviewToolHandler.ToolName, """{"repository":"repo","number":12,"base_commit":"abcdef1","head_commit":"1234567"}"""), CancellationToken.None);

        Assert.Equal(CodingToolStatus.Failed, result.Status);
        Assert.Equal("review_not_completed", ErrorCode(result));
        Assert.False(Assert.Single(h.Sink.Flags).Approved);
    }

    [Theory]
    [InlineData("""{"repository":"repo","number":0,"base_commit":"abcdef1","head_commit":"1234567"}""")]
    [InlineData("""{"repository":"repo","number":1,"base_commit":"not-hex!","head_commit":"1234567"}""")]
    [InlineData("""{"repository":"repo","number":1,"base_commit":"abc","head_commit":"1234567"}""")]
    [InlineData("""{"number":1,"base_commit":"abcdef1","head_commit":"1234567"}""")]
    [InlineData("""{"repository":"repo","number":1,"base_commit":"abcdef1","head_commit":"1234567","author_model_id":"claude-x"}""")]
    public async Task Review_rejects_malformed_arguments_including_a_model_chosen_by_the_caller(string json)
    {
        using var h = new Harness();
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(ReviewToolHandler.ToolName, json), CancellationToken.None);

        Assert.Equal("malformed_args", ErrorCode(result));
        Assert.Empty(h.Sink.Posted);
        Assert.Empty(h.Sink.Flags);
    }

    [Fact]
    public async Task Subagent_spawn_waits_for_the_child_and_marks_its_summary_untrusted()
    {
        using var h = new Harness();
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(SubagentSpawnToolHandler.ToolName, """{"task":"Check the parser","tool_allowlist":["search"],"model_id":"claude-child","worktree":"read_only","references":["doc-17"]}"""), CancellationToken.None);

        Assert.True(result.IsOk);
        var root = Root(result.Output);
        Assert.Equal("completed", root.GetProperty("status").GetString());
        Assert.Equal("done", root.GetProperty("summary").GetString());
        Assert.True(root.GetProperty("summary_is_untrusted").GetBoolean());
        Assert.Equal("trace-9", root.GetProperty("trace_id").GetString());
        Assert.Equal("session-1", h.Spawner.Last!.ParentSessionId);
        Assert.Equal(WorktreeRequest.ReadOnly, h.Spawner.Last.Worktree);
        Assert.Equal("search", Assert.Single(h.Spawner.Last.ToolAllowlist));
        Assert.Equal("doc-17", Assert.Single(h.Spawner.Last.References!));
    }

    [Fact]
    public async Task Subagent_spawn_defaults_to_no_worktree()
    {
        using var h = new Harness();
        var runner = h.Runner();

        await runner.RunAsync(Call(SubagentSpawnToolHandler.ToolName, """{"task":"t","tool_allowlist":["search"],"model_id":"claude-child"}"""), CancellationToken.None);

        Assert.Equal(WorktreeRequest.None, h.Spawner.Last!.Worktree);
    }

    [Fact]
    public async Task Subagent_spawn_failed_child_is_a_failed_result()
    {
        using var h = new Harness(spawn: _ => Task.FromResult(Harness.CompletedHandle(SubagentStatus.Failed)));
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(SubagentSpawnToolHandler.ToolName, """{"task":"t","tool_allowlist":["search"],"model_id":"claude-child"}"""), CancellationToken.None);

        Assert.Equal(CodingToolStatus.Failed, result.Status);
        Assert.Equal("failed", Root(result.Output).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Subagent_spawn_refused_by_the_limits_starts_nothing_and_says_why()
    {
        using var h = new Harness(spawn: _ => throw new SubagentRejectedException(SubagentRejection.ModelNotAllowed, "not whitelisted"));
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(SubagentSpawnToolHandler.ToolName, """{"task":"t","tool_allowlist":["search"],"model_id":"claude-other"}"""), CancellationToken.None);

        Assert.Equal(CodingToolStatus.Failed, result.Status);
        Assert.Equal("subagent_rejected_model_not_allowed", ErrorCode(result));
    }

    [Fact]
    public async Task Subagent_spawn_without_parent_limits_starts_nothing()
    {
        using var h = new Harness();
        h.Host.Parent = null;
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(SubagentSpawnToolHandler.ToolName, """{"task":"t","tool_allowlist":["search"],"model_id":"claude-child"}"""), CancellationToken.None);

        Assert.Equal("no_parent_context", ErrorCode(result));
        Assert.Null(h.Spawner.Last);
    }

    [Fact]
    public async Task Subagent_spawn_refuses_parent_limits_that_belong_to_another_session()
    {
        using var h = new Harness();
        h.Host.Parent = new ParentContext("someone-else", "chain-9", new HashSet<string>(["search"], StringComparer.Ordinal), new HashSet<string>(["claude-child"], StringComparer.Ordinal), 0);
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(SubagentSpawnToolHandler.ToolName, """{"task":"t","tool_allowlist":["search"],"model_id":"claude-child"}"""), CancellationToken.None);

        Assert.Equal("parent_mismatch", ErrorCode(result));
        Assert.Null(h.Spawner.Last);
    }

    [Theory]
    [InlineData("""{"tool_allowlist":["search"],"model_id":"claude-child"}""")]
    [InlineData("""{"task":"t","model_id":"claude-child"}""")]
    [InlineData("""{"task":"t","tool_allowlist":["search"]}""")]
    [InlineData("""{"task":"t","tool_allowlist":"search","model_id":"claude-child"}""")]
    [InlineData("""{"task":"t","tool_allowlist":["search"],"model_id":"claude-child","worktree":"everything"}""")]
    [InlineData("""{"task":"t","tool_allowlist":["search"],"model_id":"claude-child","worktree":"own","declared_paths":[3]}""")]
    public async Task Subagent_spawn_rejects_malformed_arguments(string json)
    {
        using var h = new Harness();
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(SubagentSpawnToolHandler.ToolName, json), CancellationToken.None);

        Assert.Equal("malformed_args", ErrorCode(result));
        Assert.Null(h.Spawner.Last);
    }

    [Fact]
    public async Task A_handler_exception_never_leaks_to_the_model()
    {
        using var h = new Harness(spawn: _ => throw new InvalidOperationException("internal detail /host/secret"));
        var runner = h.Runner();

        var result = await runner.RunAsync(Call(SubagentSpawnToolHandler.ToolName, """{"task":"t","tool_allowlist":["search"],"model_id":"claude-child"}"""), CancellationToken.None);

        Assert.Equal(CodingToolStatus.Failed, result.Status);
        Assert.DoesNotContain("/host/secret", result.Output, StringComparison.Ordinal);
    }

    // ---- read and list (doc 17, "Coding tool handlers", decisions 7 to 11) -------------------------------------------

    private static Task<CodingToolResult> RunReadAsync(CodingToolRunner runner, string json) =>
        runner.RunAsync(Call(ReadToolHandler.ToolName, json, _wt), CancellationToken.None).AsTask();

    private static Task<CodingToolResult> RunListAsync(CodingToolRunner runner, string json) =>
        runner.RunAsync(Call(ListToolHandler.ToolName, json, _wt), CancellationToken.None).AsTask();

    private static string Numbered(int count) => string.Concat(Enumerable.Range(1, count).Select(i => $"line{i}\n"));

    [Fact]
    public async Task Read_valid_call_returns_the_lines_as_untrusted_data()
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        h.FilesSource.Files.SetText("src/a.cs", "one\ntwo\r\nthree\n");
        var runner = h.Runner();

        var result = await RunReadAsync(runner, """{"path":"src/a.cs"}""");

        Assert.True(result.IsOk);
        Assert.Equal(0, result.ExitCode);
        var root = Root(result.Output);
        Assert.Equal("src/a.cs", root.GetProperty("path").GetString());
        Assert.Equal("one\ntwo\nthree\n", root.GetProperty("content").GetString());
        Assert.Equal(3, root.GetProperty("total_lines").GetInt32());
        Assert.Equal(3, root.GetProperty("returned_lines").GetInt32());
        Assert.Equal(1, root.GetProperty("start_line").GetInt32());
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.True(root.GetProperty("content_is_untrusted").GetBoolean());
    }

    [Fact]
    public async Task Read_start_line_and_max_lines_select_a_window_and_flag_the_cut()
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        h.FilesSource.Files.SetText("big.txt", Numbered(10));
        var runner = h.Runner();

        var result = await RunReadAsync(runner, """{"path":"big.txt","start_line":4,"max_lines":2}""");

        var root = Root(result.Output);
        Assert.Equal("line4\nline5\n", root.GetProperty("content").GetString());
        Assert.Equal(2, root.GetProperty("returned_lines").GetInt32());
        Assert.Equal(10, root.GetProperty("total_lines").GetInt32());
        Assert.True(root.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Read_start_line_past_the_end_returns_no_lines_and_is_not_truncated()
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        h.FilesSource.Files.SetText("big.txt", Numbered(3));
        var runner = h.Runner();

        var result = await RunReadAsync(runner, """{"path":"big.txt","start_line":50}""");

        Assert.True(result.IsOk);
        var root = Root(result.Output);
        Assert.Equal(string.Empty, root.GetProperty("content").GetString());
        Assert.Equal(0, root.GetProperty("returned_lines").GetInt32());
        Assert.Equal(3, root.GetProperty("total_lines").GetInt32());
        Assert.False(root.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Read_cuts_one_line_longer_than_the_character_cap_and_flags_it()
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        h.FilesSource.Files.SetText("minified.js", new string('x', ReadToolHandler.MaxOutputCharacters + 5_000));
        var runner = h.Runner();

        var result = await RunReadAsync(runner, """{"path":"minified.js"}""");

        var root = Root(result.Output);
        Assert.Equal(ReadToolHandler.MaxOutputCharacters, root.GetProperty("content").GetString()!.Length);
        Assert.True(root.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Read_refuses_a_file_over_the_size_limit_without_returning_text()
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        h.FilesSource.Files.SetBytes("huge.txt", new byte[ReadToolHandler.MaxFileBytes + 1]);
        var runner = h.Runner();

        var result = await RunReadAsync(runner, """{"path":"huge.txt"}""");

        Assert.Equal(CodingToolStatus.Failed, result.Status);
        Assert.Equal("file_too_large", ErrorCode(result));
        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain("content", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_refuses_a_binary_file()
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        h.FilesSource.Files.SetBytes("image.bin", [0x89, 0x50, 0x00, 0x47]);
        var runner = h.Runner();

        var result = await RunReadAsync(runner, """{"path":"image.bin"}""");

        Assert.Equal("binary_file", ErrorCode(result));
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Read_missing_file_is_a_structured_failure()
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        var runner = h.Runner();

        var result = await RunReadAsync(runner, """{"path":"nope.cs"}""");

        Assert.Equal("file_not_found", ErrorCode(result));
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Read_store_failure_returns_a_fixed_message_and_never_the_guest_path()
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        h.FilesSource.Files.SetText("a.cs", "x");
        h.FilesSource.Files.AfterReadHook = _ => throw new IOException("guest error at /guest/secret/a.cs");
        var runner = h.Runner();

        var result = await RunReadAsync(runner, """{"path":"a.cs"}""");

        Assert.Equal("read_failed", ErrorCode(result));
        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain("/guest/secret", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_follows_a_link_that_stays_inside_the_worktree()
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        h.FilesSource.Files.SetText("src/a.cs", "class A");
        h.FilesSource.Files.CreateSymlink("alias.cs", "src/a.cs");
        var runner = h.Runner();

        var result = await RunReadAsync(runner, """{"path":"alias.cs"}""");

        Assert.True(result.IsOk);
        Assert.Equal("class A", Root(result.Output).GetProperty("content").GetString()!.TrimEnd('\n'));
    }

    [Theory]
    [InlineData("""{"path":"../secret.txt"}""")]
    [InlineData("""{"path":"src/../../secret.txt"}""")]
    [InlineData("""{"path":"/etc/passwd"}""")]
    [InlineData("""{"path":"C:/Windows/win.ini"}""")]
    [InlineData("""{"path":"CON.txt"}""")]
    [InlineData("""{"path":"out.cs"}""")]
    [InlineData("""{"path":"abs.cs"}""")]
    public async Task Read_refuses_a_path_that_escapes_the_worktree(string json)
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        h.FilesSource.Files.CreateSymlink("out.cs", "../../secret.txt");
        h.FilesSource.Files.CreateSymlink("abs.cs", "/etc/passwd");
        var runner = h.Runner();

        var result = await RunReadAsync(runner, json);

        Assert.Equal(CodingToolStatus.Failed, result.Status);
        Assert.Contains(ErrorCode(result), _escapeOrMalformed);
        Assert.DoesNotContain("secret.txt", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("passwd", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"path":""}""")]
    [InlineData("""{"path":"."}""")]
    [InlineData("""{"path":5}""")]
    [InlineData("""{"path":"a.cs","start_line":0}""")]
    [InlineData("""{"path":"a.cs","start_line":"1"}""")]
    [InlineData("""{"path":"a.cs","max_lines":0}""")]
    [InlineData("""{"path":"a.cs","max_lines":1001}""")]
    [InlineData("""{"path":"a.cs","encoding":"utf-16"}""")]
    public async Task Read_rejects_malformed_arguments(string json)
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        h.FilesSource.Files.SetText("a.cs", "x");
        var runner = h.Runner();

        var result = await RunReadAsync(runner, json);

        Assert.Equal(CodingToolStatus.Failed, result.Status);
        Assert.Equal("malformed_args", ErrorCode(result));
        Assert.Equal(64, result.ExitCode);
        Assert.DoesNotContain("utf-16", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_without_a_bound_file_store_fails_closed()
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        h.FilesSource.Available = false;
        var runner = h.Runner();

        var result = await RunReadAsync(runner, """{"path":"a.cs"}""");

        Assert.Equal("worktree_files_unavailable", ErrorCode(result));
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task List_root_at_depth_one_shows_top_level_files_and_directories()
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        SeedTree(h.FilesSource.Files);
        var runner = h.Runner();

        var result = await RunListAsync(runner, "{}");

        Assert.True(result.IsOk);
        Assert.Equal(_rootDepthOne, Entries(result));
        Assert.False(Root(result.Output).GetProperty("truncated").GetBoolean());
        Assert.True(Root(result.Output).GetProperty("content_is_untrusted").GetBoolean());
    }

    [Fact]
    public async Task List_depth_two_adds_the_next_level_and_nothing_deeper()
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        SeedTree(h.FilesSource.Files);
        var runner = h.Runner();

        var result = await RunListAsync(runner, """{"depth":2}""");

        Assert.Equal(_rootDepthTwo, Entries(result));
    }

    [Fact]
    public async Task List_a_subdirectory_lists_its_own_children_only()
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        SeedTree(h.FilesSource.Files);
        var runner = h.Runner();

        var result = await RunListAsync(runner, """{"path":"src"}""");

        Assert.Equal("src", Root(result.Output).GetProperty("path").GetString());
        Assert.Equal(_srcDepthOne, Entries(result));
    }

    [Fact]
    public async Task List_stops_at_max_entries_and_says_so()
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        SeedTree(h.FilesSource.Files);
        var runner = h.Runner();

        var result = await RunListAsync(runner, """{"depth":8,"max_entries":2}""");

        Assert.Equal(2, Entries(result).Length);
        Assert.True(Root(result.Output).GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task List_omits_entries_that_resolve_outside_the_worktree()
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        h.FilesSource.Files.SetText("link/x.cs", "x");
        h.FilesSource.Files.CreateSymlink("link", "/etc");
        var runner = h.Runner();

        var result = await RunListAsync(runner, """{"depth":2}""");

        Assert.Empty(Entries(result));
        Assert.Equal(2, Root(result.Output).GetProperty("omitted_escaping").GetInt32());
        Assert.DoesNotContain("/etc", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"path":"../src"}""")]
    [InlineData("""{"path":"/etc"}""")]
    [InlineData("""{"path":"out"}""")]
    public async Task List_refuses_a_directory_that_escapes_the_worktree(string json)
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        h.FilesSource.Files.CreateSymlink("out", "/etc");
        var runner = h.Runner();

        var result = await RunListAsync(runner, json);

        Assert.Equal(CodingToolStatus.Failed, result.Status);
        Assert.Contains(ErrorCode(result), _escapeOrMalformed);
        Assert.DoesNotContain("/etc", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"depth":0}""")]
    [InlineData("""{"depth":9}""")]
    [InlineData("""{"depth":"deep"}""")]
    [InlineData("""{"max_entries":0}""")]
    [InlineData("""{"path":5}""")]
    [InlineData("""{"recursive":true}""")]
    public async Task List_rejects_malformed_arguments(string json)
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        var runner = h.Runner();

        var result = await RunListAsync(runner, json);

        Assert.Equal("malformed_args", ErrorCode(result));
        Assert.Equal(64, result.ExitCode);
        Assert.DoesNotContain("recursive", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_without_a_bound_file_store_fails_closed()
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        h.FilesSource.Available = false;
        var runner = h.Runner();

        var result = await RunListAsync(runner, "{}");

        Assert.Equal("worktree_files_unavailable", ErrorCode(result));
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Read_and_list_run_through_a_denying_gate_without_touching_the_store()
    {
        using var h = new Harness();
        await using var worktree = await h.OpenWorktreeAsync();
        h.FilesSource.Files.SetText("a.cs", "x");
        var runner = h.Runner(_ => new GenericPolicyDecision(PermissionOutcome.Deny, "nope"));

        var read = await RunReadAsync(runner, """{"path":"a.cs"}""");
        var list = await RunListAsync(runner, "{}");

        Assert.Equal("denied", read.ErrorCode);
        Assert.Equal("denied", list.ErrorCode);
        Assert.Equal(0, h.FilesSource.Requests);
    }

    private static readonly string[] _escapeOrMalformed = ["path_escape", "malformed_args"];
    private static readonly string[] _rootDepthOne = ["README.md:file", "src:directory"];
    private static readonly string[] _rootDepthTwo = ["README.md:file", "src:directory", "src/a.cs:file", "src/deep:directory"];
    private static readonly string[] _srcDepthOne = ["src/a.cs:file", "src/deep:directory"];

    /// <summary>Seeds a small tree: README.md, src/a.cs, src/deep/b.cs and src/deep/er/c.cs.</summary>
    private static void SeedTree(InMemoryWorktreeFiles files)
    {
        files.SetText("README.md", "# readme");
        files.SetText("src/a.cs", "class A");
        files.SetText("src/deep/b.cs", "class B");
        files.SetText("src/deep/er/c.cs", "class C");
    }

    /// <summary>The entries of a list result as "path:type", in the order the handler returned them.</summary>
    private static string[] Entries(CodingToolResult result) =>
        Root(result.Output).GetProperty("entries").EnumerateArray()
            .Select(e => $"{e.GetProperty("path").GetString()}:{e.GetProperty("type").GetString()}")
            .ToArray();
}
