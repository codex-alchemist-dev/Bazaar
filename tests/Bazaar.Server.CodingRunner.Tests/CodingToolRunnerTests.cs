using System.Text.Json;
using Bazaar.Contracts.Coding;
using Bazaar.Server.CodingEdit;
using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingPermissions;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.CodingRunner.Tests;

public sealed class CodingToolRunnerTests
{
    private static readonly JsonElement _noArgs = JsonElement.Parse("{}");

    private sealed class Rig
    {
        public InMemoryToolCallTrace Trace { get; } = new();

        public WorktreeLocks Locks { get; } = new();

        public Func<ToolCallContext, GenericPolicyDecision> Policy { get; set; } = _ => new GenericPolicyDecision(PermissionOutcome.Allow, "ok");

        public CountingRecorder Recorder { get; private set; } = null!;

        public CodingToolRunner Build(CodingToolRunnerOptions? options = null, TimeProvider? time = null, params ICodingToolHandler[] handlers)
        {
            var gate = new ToolPermissionGate(new FakeGenericToolPolicy(c => Policy(c)), Trace, new InMemoryAgentFreeze(), new CodingPermissionOptions());
            Recorder = new CountingRecorder(new ToolCallOutcomeRecorder(gate, Trace));
            return new CodingToolRunner(handlers, Recorder, Locks, options, time);
        }
    }

    private sealed class CountingRecorder(IToolCallRecorder inner) : IToolCallRecorder
    {
        public int Completes;
        public bool FailComplete;

        public ValueTask<ToolCallScope> BeginAsync(ToolCallContext context, CancellationToken cancellationToken) => inner.BeginAsync(context, cancellationToken);

        public ValueTask<ToolCallCompletion> CompleteAsync(ToolCallScope scope, ExecOutcome outcome, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Completes);
            if (FailComplete)
            {
                throw new InvalidOperationException("store down");
            }

            return inner.CompleteAsync(scope, outcome, cancellationToken);
        }
    }

    private static CodingToolCall Call(string tool, string? worktree = null) =>
        new("agent-1", "session-1", tool, _noArgs, Role: ToolSessionRole.Actor, Taint: SessionTaint.None, WorktreeKey: worktree);

    private static DelegateCodingToolHandler Handler(string name, Func<CodingToolCall, CancellationToken, Task<CodingToolOutput>> run, bool writes = false) =>
        new(name, ToolCapabilityClass.Read, writes, run);

    private static DelegateCodingToolHandler ExitWith(string name, int code, bool writes = false) =>
        Handler(name, (_, _) => Task.FromResult(new CodingToolOutput(new ExecOutcome(code), "{\"n\":1}")), writes);

    [Fact]
    public async Task Success_runs_the_handler_and_completes_the_row_with_exit_zero()
    {
        var rig = new Rig();
        var runner = rig.Build(handlers: ExitWith("read", 0));

        var result = await runner.RunAsync(Call("read"), CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(0, result.ExitCode);
        var row = Assert.Single(rig.Trace.Rows);
        Assert.Equal(ToolCallStatus.Succeeded, row.Status);
        Assert.Equal(0, row.ExitCode);
        Assert.Equal(row.Id, result.CallId);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(130)]
    public async Task Each_tool_exit_code_is_recorded_and_non_zero_is_failed(int code)
    {
        var rig = new Rig();
        var runner = rig.Build(handlers: ExitWith("shell", code));

        var result = await runner.RunAsync(Call("shell"), CancellationToken.None);

        Assert.Equal(CodingToolStatus.Failed, result.Status);
        Assert.Equal(code, result.ExitCode);
        var row = Assert.Single(rig.Trace.Rows);
        Assert.Equal(ToolCallStatus.Failed, row.Status);
        Assert.Equal(code, row.ExitCode);
    }

    [Fact]
    public async Task Timeout_from_the_handler_is_never_a_success_even_with_exit_zero()
    {
        var rig = new Rig();
        var runner = rig.Build(handlers: Handler("shell", (_, _) => Task.FromResult(new CodingToolOutput(new ExecOutcome(0, TimedOut: true), "{}"))));

        var result = await runner.RunAsync(Call("shell"), CancellationToken.None);

        Assert.Equal(CodingToolStatus.TimedOut, result.Status);
        Assert.Equal(ToolCallStatus.TimedOut, Assert.Single(rig.Trace.Rows).Status);
    }

    [Fact]
    public async Task Unknown_tool_returns_a_structured_error_and_is_traced()
    {
        var rig = new Rig();
        var runner = rig.Build(handlers: ExitWith("read", 0));

        var result = await runner.RunAsync(Call("rm_rf"), CancellationToken.None);

        Assert.Equal(CodingToolStatus.UnknownTool, result.Status);
        Assert.Equal("unknown_tool", result.ErrorCode);
        Assert.Equal(CodingToolRunner.UnknownToolExitCode, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Output);
        Assert.Equal("unknown_tool", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        var row = Assert.Single(rig.Trace.Rows);
        Assert.Equal("rm_rf", row.Tool);
        Assert.Equal(ToolCallStatus.Failed, row.Status);
        Assert.Equal(1, rig.Recorder.Completes);
    }

    [Fact]
    public async Task Denied_gate_never_runs_the_handler_and_returns_a_structured_error()
    {
        var rig = new Rig { Policy = _ => new GenericPolicyDecision(PermissionOutcome.Deny, "nope") };
        var ran = false;
        var runner = rig.Build(handlers: Handler("read", (_, _) => { ran = true; return Task.FromResult(new CodingToolOutput(new ExecOutcome(0), "{}")); }));

        var result = await runner.RunAsync(Call("read"), CancellationToken.None);

        Assert.False(ran);
        Assert.Equal(CodingToolStatus.Denied, result.Status);
        Assert.Equal("denied", result.ErrorCode);
        Assert.Equal(ToolCallStatus.Refused, Assert.Single(rig.Trace.Rows).Status);
    }

    [Fact]
    public async Task Approval_needed_is_reported_and_the_handler_does_not_run()
    {
        var rig = new Rig { Policy = _ => new GenericPolicyDecision(PermissionOutcome.RequireApproval, "needs owner") };
        var ran = false;
        var runner = rig.Build(handlers: Handler("read", (_, _) => { ran = true; return Task.FromResult(new CodingToolOutput(new ExecOutcome(0), "{}")); }));

        var result = await runner.RunAsync(Call("read"), CancellationToken.None);

        Assert.False(ran);
        Assert.Equal(CodingToolStatus.ApprovalRequired, result.Status);
    }

    [Fact]
    public async Task Trace_write_failure_fails_closed_without_running()
    {
        var rig = new Rig();
        rig.Trace.FailWrites = true;
        var ran = false;
        var runner = rig.Build(handlers: Handler("read", (_, _) => { ran = true; return Task.FromResult(new CodingToolOutput(new ExecOutcome(0), "{}")); }));

        var result = await runner.RunAsync(Call("read"), CancellationToken.None);

        Assert.False(ran);
        Assert.False(result.IsOk);
        Assert.Equal(CodingToolStatus.Denied, result.Status);
    }

    [Fact]
    public async Task Handler_exception_is_recorded_and_not_thrown_through()
    {
        var rig = new Rig();
        var runner = rig.Build(handlers: Handler("read", (_, _) => throw new InvalidOperationException("secret path /etc/shadow")));

        var result = await runner.RunAsync(Call("read"), CancellationToken.None);

        Assert.Equal(CodingToolStatus.Failed, result.Status);
        Assert.DoesNotContain("shadow", result.Output, StringComparison.Ordinal);
        Assert.Equal(1, rig.Recorder.Completes);
        var row = Assert.Single(rig.Trace.Rows);
        Assert.Equal(ToolCallStatus.Failed, row.Status);
        Assert.Equal(CodingToolRunner.HandlerFailedExitCode, row.ExitCode);
    }

    [Fact]
    public async Task Cancellation_records_the_outcome_then_rethrows()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();
        var runner = rig.Build(handlers: Handler("shell", async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return new CodingToolOutput(new ExecOutcome(0), "{}");
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await runner.RunAsync(Call("shell"), cts.Token));

        Assert.Equal(1, rig.Recorder.Completes);
        Assert.Equal(ToolCallStatus.Failed, Assert.Single(rig.Trace.Rows).Status);
    }

    [Fact]
    public async Task Handler_own_timeout_exception_is_a_timeout_not_a_cancel()
    {
        var rig = new Rig();
        var runner = rig.Build(handlers: Handler("shell", (_, _) => throw new OperationCanceledException()));

        var result = await runner.RunAsync(Call("shell"), CancellationToken.None);

        Assert.Equal(CodingToolStatus.TimedOut, result.Status);
        Assert.Equal(ToolCallStatus.TimedOut, Assert.Single(rig.Trace.Rows).Status);
    }

    [Fact]
    public async Task Recorder_completion_failure_is_reported_without_throwing()
    {
        var rig = new Rig();
        var runner = rig.Build(handlers: ExitWith("read", 0));
        rig.Recorder.FailComplete = true;

        var result = await runner.RunAsync(Call("read"), CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.True(result.TraceFailed);
    }

    [Fact]
    public async Task Writer_without_a_worktree_key_fails_closed_after_tracing()
    {
        var rig = new Rig();
        var runner = rig.Build(handlers: ExitWith("edit", 0, writes: true));

        var result = await runner.RunAsync(Call("edit"), CancellationToken.None);

        Assert.Equal("worktree_required", result.ErrorCode);
        Assert.Equal(1, rig.Recorder.Completes);
        Assert.Equal(CodingToolRunner.MissingWorktreeExitCode, Assert.Single(rig.Trace.Rows).ExitCode);
    }

    [Fact]
    public async Task Two_writers_on_one_worktree_never_overlap()
    {
        var rig = new Rig();
        var active = 0;
        var maxActive = 0;
        var runner = rig.Build(handlers: Handler("edit", async (_, ct) =>
        {
            var now = Interlocked.Increment(ref active);
            InterlockedMax(ref maxActive, now);
            await Task.Delay(60, ct);
            Interlocked.Decrement(ref active);
            return new CodingToolOutput(new ExecOutcome(0), "{}");
        }, writes: true));

        var results = await Task.WhenAll(
            runner.RunAsync(Call("edit", "wt-1"), CancellationToken.None).AsTask(),
            runner.RunAsync(Call("edit", "wt-1"), CancellationToken.None).AsTask());

        Assert.All(results, r => Assert.True(r.IsOk));
        Assert.Equal(1, maxActive);
    }

    [Fact]
    public async Task Writers_on_different_worktrees_run_together()
    {
        var rig = new Rig();
        var active = 0;
        var maxActive = 0;
        var runner = rig.Build(handlers: Handler("edit", async (_, ct) =>
        {
            var now = Interlocked.Increment(ref active);
            InterlockedMax(ref maxActive, now);
            await Task.Delay(100, ct);
            Interlocked.Decrement(ref active);
            return new CodingToolOutput(new ExecOutcome(0), "{}");
        }, writes: true));

        await Task.WhenAll(
            runner.RunAsync(Call("edit", "wt-1"), CancellationToken.None).AsTask(),
            runner.RunAsync(Call("edit", "wt-2"), CancellationToken.None).AsTask());

        Assert.Equal(2, maxActive);
    }

    [Fact]
    public async Task Busy_worktree_returns_busy_after_the_wait_and_closes_the_row()
    {
        var rig = new Rig();
        var options = new CodingToolRunnerOptions { LockWaitTimeout = TimeSpan.FromMilliseconds(80), LockPollInterval = TimeSpan.FromMilliseconds(10) };
        var runner = rig.Build(options, handlers: ExitWith("edit", 0, writes: true));
        await using var held = await rig.Locks.TryAcquireAsync("wt-1", TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.NotNull(held);

        var result = await runner.RunAsync(Call("edit", "wt-1"), CancellationToken.None);

        Assert.Equal(CodingToolStatus.Busy, result.Status);
        Assert.Equal(CodingToolRunner.BusyExitCode, Assert.Single(rig.Trace.Rows).ExitCode);
    }

    [Fact]
    public async Task Lock_is_released_after_a_handler_exception()
    {
        var rig = new Rig();
        var runner = rig.Build(handlers: Handler("edit", (_, _) => throw new InvalidOperationException("boom"), writes: true));

        await runner.RunAsync(Call("edit", "wt-1"), CancellationToken.None);

        await using var lease = await rig.Locks.TryAcquireAsync("wt-1", TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task Cancelling_while_waiting_for_the_lock_closes_the_row_and_rethrows()
    {
        var rig = new Rig();
        var runner = rig.Build(handlers: ExitWith("edit", 0, writes: true));
        await using var held = await rig.Locks.TryAcquireAsync("wt-1", TimeSpan.FromMinutes(5), CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await runner.RunAsync(Call("edit", "wt-1"), cts.Token));

        Assert.Equal(1, rig.Recorder.Completes);
    }

    [Fact]
    public void Duplicate_or_blank_handler_names_are_rejected()
    {
        var rig = new Rig();
        Assert.Throws<ArgumentException>(() => rig.Build(handlers: [ExitWith("a", 0), ExitWith("a", 0)]));
        Assert.Throws<ArgumentException>(() => rig.Build(handlers: ExitWith(" a", 0)));
    }

    [Fact]
    public async Task Edit_handler_applies_a_whole_file_edit_and_a_malformed_edit_is_a_failed_result()
    {
        var files = new InMemoryWorktreeFiles();
        var router = new EditToolRouter([new WholeFileEditTool(files)], new EditMetrics());
        var rig = new Rig();
        var runner = rig.Build(handlers: new EditToolHandler(router, _ => EditFormat.WholeFile));

        var ok = await runner.RunAsync(Call("edit", "wt-1") with { Args = Parse("""{"path":"a.txt","content":"hi"}""") }, CancellationToken.None);
        var bad = await runner.RunAsync(Call("edit", "wt-1") with { Args = Parse("""{"path":"a.txt"}""") }, CancellationToken.None);
        var escape = await runner.RunAsync(Call("edit", "wt-1") with { Args = Parse("""{"path":"../x","content":"hi"}""") }, CancellationToken.None);

        Assert.True(ok.IsOk);
        Assert.Equal("hi", files.GetText("a.txt"));
        Assert.Equal(CodingToolStatus.Failed, bad.Status);
        Assert.Equal(CodingToolStatus.Failed, escape.Status);
        Assert.Contains("PathEscape", escape.Output, StringComparison.Ordinal);
    }

    private static JsonElement Parse(string json) => JsonElement.Parse(json);

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
}
