using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Subagents.Tests;

public sealed class SubagentSpawnerTests
{
    private static readonly string[] _parentTools = ["read", "grep", "edit", "exec", "spawn_subagent", "merge"];

    private sealed class Rig
    {
        public FakeRuntimeSubagentPort Runtime { get; } = new();

        public InMemorySubagentTrace Trace { get; } = new();

        public InMemoryWorktreeAllocator Allocator { get; } = new();

        public SubagentSpawner Spawner { get; }

        public Rig(SubagentOptions? options = null)
        {
            Spawner = new SubagentSpawner(options ?? new SubagentOptions(), Runtime, Allocator, Trace);
        }
    }

    private static ParentContext Parent(int depth = 0, string id = "p1") =>
        new(id, "chain", new HashSet<string>(_parentTools), new HashSet<string> { "haiku", "sonnet" }, depth);

    private static SubagentSpec Spec(
        string[]? tools = null,
        WorktreeRequest wt = WorktreeRequest.ReadOnly,
        string[]? paths = null,
        string model = "haiku",
        string parent = "p1") =>
        new(parent, "review the diff", new HashSet<string>(tools ?? ["read", "grep"]), model, wt, paths, ["ref-1"]);

    private static async Task WaitRunning(Rig rig, int count)
    {
        while (rig.Runtime.Running < count)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    private static async Task<SubagentRejectedException> Rejected(Task<SubagentHandle> t) =>
        await Assert.ThrowsAsync<SubagentRejectedException>(() => t);

    [Fact]
    public async Task Spawn_runs_child_with_task_and_references_only()
    {
        var rig = new Rig();
        var handle = await rig.Spawner.SpawnAsync(Parent(), Spec(), CancellationToken.None);
        var result = await handle.Completion;

        Assert.Equal(SubagentStatus.Completed, result.Status);
        var call = Assert.Single(rig.Runtime.Calls);
        Assert.Equal("review the diff", call.Task);
        Assert.Equal(["ref-1"], call.References);
        Assert.Equal(0, call.ParentDepth);
        Assert.Equal(["grep", "read"], call.Tools.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Tools_not_subset_of_parent_are_rejected_not_intersected()
    {
        var rig = new Rig();
        var ex = await Rejected(rig.Spawner.SpawnAsync(Parent(), Spec(["read", "browser"]), CancellationToken.None));
        Assert.Equal(SubagentRejection.ToolsNotSubset, ex.Reason);
        Assert.Empty(rig.Runtime.Calls);
        Assert.Empty(rig.Trace.Entries);
    }

    [Fact]
    public async Task Model_outside_parent_whitelist_is_rejected()
    {
        var rig = new Rig();
        var ex = await Rejected(rig.Spawner.SpawnAsync(Parent(), Spec(model: "opus"), CancellationToken.None));
        Assert.Equal(SubagentRejection.ModelNotAllowed, ex.Reason);
        Assert.Empty(rig.Runtime.Calls);
    }

    [Fact]
    public async Task Spec_naming_another_parent_is_rejected()
    {
        var rig = new Rig();
        var ex = await Rejected(rig.Spawner.SpawnAsync(Parent(), Spec(parent: "other"), CancellationToken.None));
        Assert.Equal(SubagentRejection.InvalidSpec, ex.Reason);
    }

    [Fact]
    public async Task Default_depth_allows_children_of_top_level_only()
    {
        var rig = new Rig();
        var ex = await Rejected(rig.Spawner.SpawnAsync(Parent(depth: 1), Spec(), CancellationToken.None));
        Assert.Equal(SubagentRejection.DepthExceeded, ex.Reason);
        Assert.Empty(rig.Runtime.Calls);
    }

    [Fact]
    public async Task Child_at_max_depth_loses_the_spawn_tool()
    {
        var rig = new Rig();
        var handle = await rig.Spawner.SpawnAsync(Parent(), Spec(["read", "spawn_subagent"]), CancellationToken.None);
        await handle.Completion;
        Assert.DoesNotContain("spawn_subagent", Assert.Single(rig.Runtime.Calls).Tools);
        Assert.DoesNotContain("spawn_subagent", Assert.Single(rig.Trace.Entries, e => e.Kind == SubagentTraceKind.Spawned).Tools);
    }

    [Fact]
    public async Task Depth_two_keeps_spawn_tool_for_depth_one_child_and_stops_at_two()
    {
        var rig = new Rig(new SubagentOptions { MaxDepth = 2 });
        var h = await rig.Spawner.SpawnAsync(Parent(), Spec(["read", "spawn_subagent"]), CancellationToken.None);
        await h.Completion;
        Assert.Contains("spawn_subagent", Assert.Single(rig.Runtime.Calls).Tools);

        var h2 = await rig.Spawner.SpawnAsync(Parent(depth: 1), Spec(["read", "spawn_subagent"]), CancellationToken.None);
        await h2.Completion;
        Assert.DoesNotContain("spawn_subagent", rig.Runtime.Calls.Last().Tools);

        var ex = await Rejected(rig.Spawner.SpawnAsync(Parent(depth: 2), Spec(), CancellationToken.None));
        Assert.Equal(SubagentRejection.DepthExceeded, ex.Reason);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(0)]
    public void Options_cannot_exceed_runtime_depth_or_drop_below_one(int depth)
    {
        var options = new SubagentOptions { MaxDepth = depth };
        Assert.NotEmpty(options.Validate());
        Assert.Throws<ArgumentException>(() => new SubagentSpawner(options, new FakeRuntimeSubagentPort(), new InMemoryWorktreeAllocator(), new InMemorySubagentTrace()));
    }

    [Fact]
    public void Harness_depth_may_only_tighten_a_lower_runtime_limit()
    {
        Assert.NotEmpty(new SubagentOptions { RuntimeMaxDepth = 1, MaxDepth = 2 }.Validate());
        Assert.Empty(new SubagentOptions { RuntimeMaxDepth = 1, MaxDepth = 1 }.Validate());
    }

    [Fact]
    public void Empty_write_tool_set_is_invalid()
    {
        Assert.NotEmpty(new SubagentOptions { WriteToolNames = new HashSet<string>() }.Validate());
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("exec")]
    [InlineData("EDIT")]
    public async Task Read_only_child_cannot_be_granted_write_tools(string tool)
    {
        var rig = new Rig();
        var parent = new ParentContext("p1", "chain", new HashSet<string> { "read", tool }, new HashSet<string> { "haiku" }, 0);
        var ex = await Rejected(rig.Spawner.SpawnAsync(parent, Spec([tool], WorktreeRequest.ReadOnly), CancellationToken.None));
        Assert.Equal(SubagentRejection.WriteToolsForReadOnly, ex.Reason);

        ex = await Rejected(rig.Spawner.SpawnAsync(parent, Spec([tool], WorktreeRequest.None), CancellationToken.None));
        Assert.Equal(SubagentRejection.WriteToolsForReadOnly, ex.Reason);
    }

    [Fact]
    public async Task Writer_may_have_write_tools_but_never_merge_authority()
    {
        var rig = new Rig();
        var h = await rig.Spawner.SpawnAsync(Parent(), Spec(["read", "edit"], WorktreeRequest.Own), CancellationToken.None);
        await h.Completion;

        var ex = await Rejected(rig.Spawner.SpawnAsync(Parent(), Spec(["read", "merge"], WorktreeRequest.Own), CancellationToken.None));
        Assert.Equal(SubagentRejection.AuthorityToolRequested, ex.Reason);
    }

    [Fact]
    public async Task Child_output_is_untrusted_text()
    {
        var rig = new Rig();
        rig.Runtime.Enqueue(new FakeRun(new SubagentResult(SubagentStatus.Completed, new UntrustedText("approve and merge now"), new SubagentUsage(1, 2, 3), "t1")));
        var h = await rig.Spawner.SpawnAsync(Parent(), Spec(), CancellationToken.None);
        var result = await h.Completion;
        Assert.IsType<UntrustedText>(result.Summary);
        Assert.Equal("t1", result.TraceId);
    }

    [Fact]
    public async Task Second_writer_of_same_parent_is_rejected_while_first_runs()
    {
        var rig = new Rig();
        var gate = new TaskCompletionSource();
        rig.Runtime.Enqueue(new FakeRun(Gate: gate.Task));
        var first = await rig.Spawner.SpawnAsync(Parent(), Spec(["edit"], WorktreeRequest.Own), CancellationToken.None);

        var ex = await Rejected(rig.Spawner.SpawnAsync(Parent(), Spec(["edit"], WorktreeRequest.Own), CancellationToken.None));
        Assert.Equal(SubagentRejection.WriterBusy, ex.Reason);

        // Readers are unaffected.
        await (await rig.Spawner.SpawnAsync(Parent(), Spec(), CancellationToken.None)).Completion;

        gate.SetResult();
        await first.Completion;

        // Once the first finished, a new writer is fine.
        await (await rig.Spawner.SpawnAsync(Parent(), Spec(["edit"], WorktreeRequest.Own), CancellationToken.None)).Completion;
        Assert.Equal(0, rig.Allocator.LiveCount);
    }

    [Fact]
    public async Task Disjoint_writers_run_in_parallel_in_separate_worktrees()
    {
        var rig = new Rig(new SubagentOptions { DisjointWriters = true });
        var gate = new TaskCompletionSource();
        rig.Runtime.Enqueue(new FakeRun(Gate: gate.Task));
        rig.Runtime.Enqueue(new FakeRun(Gate: gate.Task));
        var a = await rig.Spawner.SpawnAsync(Parent(), Spec(["edit"], WorktreeRequest.Own, ["src/a", "docs\\x/"]), CancellationToken.None);
        var b = await rig.Spawner.SpawnAsync(Parent(), Spec(["edit"], WorktreeRequest.Own, ["src/b"]), CancellationToken.None);
        await WaitRunning(rig, 2);
        Assert.Equal(2, rig.Runtime.Running);
        Assert.Equal(2, rig.Runtime.Calls.Select(c => c.Worktree!.Path).Distinct().Count());
        gate.SetResult();
        await Task.WhenAll(a.Completion, b.Completion);
        Assert.Equal(0, rig.Allocator.LiveCount);
    }

    [Theory]
    [InlineData("src/a", "src/a")]
    [InlineData("src/a", "src/a/deep/file.cs")]
    [InlineData("src/a/deep", "SRC/A")]
    public async Task Overlapping_declared_paths_are_rejected_up_front(string first, string second)
    {
        var rig = new Rig(new SubagentOptions { DisjointWriters = true });
        var gate = new TaskCompletionSource();
        rig.Runtime.Enqueue(new FakeRun(Gate: gate.Task));
        var a = await rig.Spawner.SpawnAsync(Parent(), Spec(["edit"], WorktreeRequest.Own, [first]), CancellationToken.None);
        await WaitRunning(rig, 1);

        var ex = await Rejected(rig.Spawner.SpawnAsync(Parent(), Spec(["edit"], WorktreeRequest.Own, [second]), CancellationToken.None));
        Assert.Equal(SubagentRejection.PathsOverlap, ex.Reason);
        Assert.Single(rig.Runtime.Calls);
        Assert.Equal(1, rig.Allocator.LiveCount);

        gate.SetResult();
        await a.Completion;
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("/abs")]
    [InlineData("C:/x")]
    [InlineData("a/../b")]
    [InlineData(".")]
    public async Task Unsafe_declared_paths_are_rejected(string path)
    {
        var rig = new Rig(new SubagentOptions { DisjointWriters = true });
        var ex = await Rejected(rig.Spawner.SpawnAsync(Parent(), Spec(["edit"], WorktreeRequest.Own, [path]), CancellationToken.None));
        Assert.Equal(SubagentRejection.PathsOverlap, ex.Reason);
    }

    [Fact]
    public async Task Disjoint_writer_without_declared_paths_is_rejected()
    {
        var rig = new Rig(new SubagentOptions { DisjointWriters = true });
        var ex = await Rejected(rig.Spawner.SpawnAsync(Parent(), Spec(["edit"], WorktreeRequest.Own), CancellationToken.None));
        Assert.Equal(SubagentRejection.PathsOverlap, ex.Reason);
    }

    [Fact]
    public async Task Allocator_handing_out_a_held_worktree_is_refused()
    {
        var rig = new Rig(new SubagentOptions { DisjointWriters = true });
        rig.Allocator.ForcedPath = "/shared";
        var gate = new TaskCompletionSource();
        rig.Runtime.Enqueue(new FakeRun(Gate: gate.Task));
        var a = await rig.Spawner.SpawnAsync(Parent(), Spec(["edit"], WorktreeRequest.Own, ["a"]), CancellationToken.None);

        var ex = await Rejected(rig.Spawner.SpawnAsync(Parent(), Spec(["edit"], WorktreeRequest.Own, ["b"]), CancellationToken.None));
        Assert.Equal(SubagentRejection.WorktreeInUse, ex.Reason);
        gate.SetResult();
        await a.Completion;
    }

    [Fact]
    public async Task Allocator_failure_denies_spawn_and_frees_the_writer_slot()
    {
        var rig = new Rig();
        rig.Allocator.Fail = true;
        var ex = await Rejected(rig.Spawner.SpawnAsync(Parent(), Spec(["edit"], WorktreeRequest.Own), CancellationToken.None));
        Assert.Equal(SubagentRejection.WorktreeUnavailable, ex.Reason);

        rig.Allocator.Fail = false;
        await (await rig.Spawner.SpawnAsync(Parent(), Spec(["edit"], WorktreeRequest.Own), CancellationToken.None)).Completion;
    }

    [Fact]
    public async Task Trace_failure_denies_spawn_and_nothing_runs()
    {
        var rig = new Rig();
        rig.Trace.FailOn = SubagentTraceKind.Spawned;
        var ex = await Rejected(rig.Spawner.SpawnAsync(Parent(), Spec(["edit"], WorktreeRequest.Own), CancellationToken.None));
        Assert.Equal(SubagentRejection.TraceUnavailable, ex.Reason);
        Assert.Empty(rig.Runtime.Calls);
        Assert.Equal(0, rig.Allocator.LiveCount);

        // The writer reservation was released too.
        rig.Trace.FailOn = null;
        await (await rig.Spawner.SpawnAsync(Parent(), Spec(["edit"], WorktreeRequest.Own), CancellationToken.None)).Completion;
    }

    [Fact]
    public async Task Spawn_row_is_written_before_the_runtime_starts()
    {
        var trace = new OrderTrace();
        var runtime = new OrderRuntime(trace);
        var spawner = new SubagentSpawner(new SubagentOptions(), runtime, new InMemoryWorktreeAllocator(), trace);
        await (await spawner.SpawnAsync(Parent(), Spec(), CancellationToken.None)).Completion;
        Assert.Equal(["spawned", "run", "finished"], trace.Log);
    }

    [Fact]
    public async Task Finish_trace_failure_withholds_the_result()
    {
        var rig = new Rig();
        rig.Trace.FailOn = SubagentTraceKind.Finished;
        rig.Runtime.Enqueue(new FakeRun(new SubagentResult(SubagentStatus.Completed, new UntrustedText("secret"), SubagentUsage.None)));
        var result = await (await rig.Spawner.SpawnAsync(Parent(), Spec(["edit"], WorktreeRequest.Own), CancellationToken.None)).Completion;
        Assert.Equal(SubagentStatus.Failed, result.Status);
        Assert.DoesNotContain("secret", result.Summary.Value);
        Assert.Equal(0, rig.Allocator.LiveCount);
    }

    [Fact]
    public async Task Runner_exception_becomes_failed_result_and_releases_everything()
    {
        var rig = new Rig();
        rig.Runtime.Enqueue(new FakeRun(Throws: new InvalidOperationException("boom with details")));
        var result = await (await rig.Spawner.SpawnAsync(Parent(), Spec(["edit"], WorktreeRequest.Own), CancellationToken.None)).Completion;
        Assert.Equal(SubagentStatus.Failed, result.Status);
        Assert.DoesNotContain("boom", result.Summary.Value);
        Assert.Equal(0, rig.Allocator.LiveCount);
        Assert.Equal(1, rig.Allocator.ReleasedCount);
        Assert.Contains(rig.Trace.Entries, e => e.Kind == SubagentTraceKind.Finished && e.Status == SubagentStatus.Failed);

        // Writer reservation is gone.
        await (await rig.Spawner.SpawnAsync(Parent(), Spec(["edit"], WorktreeRequest.Own), CancellationToken.None)).Completion;
    }

    [Fact]
    public async Task Handle_cancel_ends_run_as_cancelled_and_releases_everything()
    {
        var rig = new Rig();
        rig.Runtime.Enqueue(new FakeRun(Delay: TimeSpan.FromMinutes(5)));
        var h = await rig.Spawner.SpawnAsync(Parent(), Spec(["edit"], WorktreeRequest.Own), CancellationToken.None);
        h.Cancel();
        var result = await h.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(SubagentStatus.Cancelled, result.Status);
        Assert.Equal(0, rig.Allocator.LiveCount);
        await (await rig.Spawner.SpawnAsync(Parent(), Spec(["edit"], WorktreeRequest.Own), CancellationToken.None)).Completion;
    }

    [Fact]
    public async Task Cancelled_spawn_token_starts_nothing()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Spawner.SpawnAsync(Parent(), Spec(), cts.Token));
        Assert.Empty(rig.Runtime.Calls);
    }

    [Fact]
    public async Task Runtime_refusal_surfaces_as_failed_result_with_reason()
    {
        var rig = new Rig();
        rig.Runtime.Enqueue(new FakeRun(Throws: new SubagentRejectedException(SubagentRejection.RuntimeLimit, "concurrency")));
        var result = await (await rig.Spawner.SpawnAsync(Parent(), Spec(), CancellationToken.None)).Completion;
        Assert.Equal(SubagentStatus.Failed, result.Status);
        Assert.Contains("RuntimeLimit", result.Summary.Value);
    }

    [Fact]
    public async Task Parallel_readers_all_complete_and_release_state()
    {
        var rig = new Rig();
        var handles = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ =>
            Task.Run(() => rig.Spawner.SpawnAsync(Parent(), Spec(), CancellationToken.None))));
        await Task.WhenAll(handles.Select(h => h.Completion));
        Assert.Equal(50, rig.Trace.Entries.Count(e => e.Kind == SubagentTraceKind.Finished));
    }

    [Fact]
    public async Task Writer_stress_allows_exactly_one_writer_per_parent_at_a_time()
    {
        var rig = new Rig();
        var accepted = 0;
        var rejected = 0;
        var tasks = Enumerable.Range(0, 100).Select(_ => Task.Run(async () =>
        {
            try
            {
                var h = await rig.Spawner.SpawnAsync(Parent(), Spec(["edit"], WorktreeRequest.Own), CancellationToken.None);
                Interlocked.Increment(ref accepted);
                await h.Completion;
            }
            catch (SubagentRejectedException ex) when (ex.Reason == SubagentRejection.WriterBusy)
            {
                Interlocked.Increment(ref rejected);
            }
        }));
        await Task.WhenAll(tasks);
        Assert.Equal(100, accepted + rejected);
        Assert.True(accepted >= 1);
        Assert.Equal(1, rig.Runtime.MaxRunning);
        Assert.Equal(0, rig.Allocator.LiveCount);
    }

    [Fact]
    public async Task Di_helper_registers_spawner_and_validates_options()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRuntimeSubagentPort>(new FakeRuntimeSubagentPort());
        services.AddSingleton<ISubagentWorktreeAllocator>(new InMemoryWorktreeAllocator());
        services.AddSingleton<ISubagentTrace>(new InMemorySubagentTrace());
        services.AddBazaarSubagents(b => b.MaxDepth = 2);
        using var provider = services.BuildServiceProvider();
        var spawner = provider.GetRequiredService<ISubagentSpawner>();
        var h = await spawner.SpawnAsync(Parent(depth: 1), Spec(), CancellationToken.None);
        await h.Completion;

        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddBazaarSubagents(b => b.MaxDepth = 3));
    }

    private sealed class OrderTrace : ISubagentTrace
    {
        public List<string> Log { get; } = [];

        public Task AppendAsync(SubagentTraceEntry entry, CancellationToken cancellationToken)
        {
            lock (Log)
            {
                Log.Add(entry.Kind == SubagentTraceKind.Spawned ? "spawned" : "finished");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class OrderRuntime(OrderTrace trace) : IRuntimeSubagentPort
    {
        public Task<SubagentResult> RunAsync(RuntimeSubagentCall invocation, CancellationToken cancellationToken)
        {
            lock (trace.Log)
            {
                trace.Log.Add("run");
            }

            return Task.FromResult(new SubagentResult(SubagentStatus.Completed, UntrustedText.Empty, SubagentUsage.None));
        }
    }
}
