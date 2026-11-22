using Bazaar.Contracts.Coding;
using Bazaar.Server.CodingPermissions;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.CodingPermissions.Tests;

public sealed class ToolCallOutcomeRecorderTests
{
    private static readonly string _hash = new('a', 64);

    private static ToolCallContext Ctx(ToolCapabilityClass cls = ToolCapabilityClass.Exec, string? branch = null) =>
        new("agent-1", "s1", "run_command", cls, _hash, "agent", SessionTaint.None, null, branch);

    private static (ToolCallOutcomeRecorder Recorder, InMemoryToolCallTrace Trace) Make(PermissionOutcome outcome = PermissionOutcome.Allow)
    {
        var trace = new InMemoryToolCallTrace();
        var gate = new ToolPermissionGate(
            new FakeGenericToolPolicy(_ => new GenericPolicyDecision(outcome, "p")),
            trace,
            new InMemoryAgentFreeze(),
            new CodingPermissionOptions());
        return (new ToolCallOutcomeRecorder(gate, trace), trace);
    }

    public static TheoryData<ExecOutcome, ToolCallStatus> Mapping => new()
    {
        { new ExecOutcome(0), ToolCallStatus.Succeeded },
        { new ExecOutcome(1), ToolCallStatus.Failed },
        { new ExecOutcome(-1), ToolCallStatus.Failed },
        { new ExecOutcome(0, TimedOut: true), ToolCallStatus.TimedOut },
        { new ExecOutcome(null, TimedOut: true), ToolCallStatus.TimedOut },
        { new ExecOutcome(0, Cancelled: true), ToolCallStatus.Failed },
        { new ExecOutcome(null, Cancelled: true), ToolCallStatus.Failed },
        { new ExecOutcome(0, TimedOut: true, Cancelled: true), ToolCallStatus.TimedOut },
        { new ExecOutcome(0, Crashed: true), ToolCallStatus.Unknown },
        { new ExecOutcome(null, TimedOut: true, Crashed: true), ToolCallStatus.Unknown },
        { new ExecOutcome(null), ToolCallStatus.Unknown },
    };

    [Theory]
    [MemberData(nameof(Mapping))]
    public void Map_covers_every_outcome(ExecOutcome outcome, ToolCallStatus expected) =>
        Assert.Equal(expected, ToolCallOutcomeRecorder.Map(outcome));

    [Theory]
    [MemberData(nameof(Mapping))]
    public async Task Completion_writes_the_mapped_status_to_the_row(ExecOutcome outcome, ToolCallStatus expected)
    {
        var (recorder, trace) = Make();
        var scope = await recorder.BeginAsync(Ctx(), CancellationToken.None);
        Assert.True(scope.MayRun);

        var done = await recorder.CompleteAsync(scope, outcome, CancellationToken.None);

        Assert.Equal(expected, done.Status);
        Assert.False(done.TraceFailed);
        var row = Assert.Single(trace.Rows);
        if (expected == ToolCallStatus.Unknown)
        {
            // A crash leaves the row as the gate wrote it: unknown and still open.
            Assert.False(done.Recorded);
            Assert.Equal(ToolCallStatus.Unknown, row.Status);
            Assert.Null(row.EndedAt);
        }
        else
        {
            Assert.True(done.Recorded);
            Assert.Equal(expected, row.Status);
            Assert.Equal(outcome.ExitCode, row.ExitCode);
            Assert.NotNull(row.EndedAt);
        }
    }

    [Fact]
    public async Task A_second_completion_returns_the_first_status_and_writes_nothing()
    {
        var (recorder, trace) = Make();
        var scope = await recorder.BeginAsync(Ctx(), CancellationToken.None);
        await recorder.CompleteAsync(scope, new ExecOutcome(1), CancellationToken.None);

        var again = await recorder.CompleteAsync(scope, new ExecOutcome(0), CancellationToken.None);

        Assert.True(again.AlreadyCompleted);
        Assert.False(again.Recorded);
        Assert.Equal(ToolCallStatus.Failed, again.Status);
        Assert.Equal(ToolCallStatus.Failed, Assert.Single(trace.Rows).Status);
    }

    [Fact]
    public async Task Concurrent_completions_write_once()
    {
        var (recorder, trace) = Make();
        var scope = await recorder.BeginAsync(Ctx(), CancellationToken.None);

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            recorder.CompleteAsync(scope, new ExecOutcome(0), CancellationToken.None).AsTask()));

        Assert.Equal(1, results.Count(r => r.Recorded));
        Assert.Equal(ToolCallStatus.Succeeded, Assert.Single(trace.Rows).Status);
    }

    [Theory]
    [InlineData(PermissionOutcome.Deny)]
    [InlineData(PermissionOutcome.RequireApproval)]
    public async Task A_refused_call_never_completes_as_success(PermissionOutcome outcome)
    {
        var (recorder, trace) = Make(outcome);
        var scope = await recorder.BeginAsync(Ctx(), CancellationToken.None);
        Assert.False(scope.MayRun);

        var done = await recorder.CompleteAsync(scope, new ExecOutcome(0), CancellationToken.None);

        Assert.Equal(ToolCallStatus.Refused, done.Status);
        Assert.False(done.Recorded);
        Assert.Equal(ToolCallStatus.Refused, Assert.Single(trace.Rows).Status);
    }

    [Fact]
    public async Task A_call_the_trace_could_not_record_cannot_run_or_complete()
    {
        var (recorder, trace) = Make();
        trace.FailWrites = true;
        var scope = await recorder.BeginAsync(Ctx(), CancellationToken.None);
        Assert.False(scope.MayRun);
        Assert.Null(scope.CallId);

        var done = await recorder.CompleteAsync(scope, new ExecOutcome(0), CancellationToken.None);

        Assert.NotEqual(ToolCallStatus.Succeeded, done.Status);
        Assert.Empty(trace.Rows);
    }

    [Fact]
    public async Task A_failed_trace_completion_is_flagged_not_hidden_and_can_be_retried()
    {
        var (recorder, trace) = Make();
        var scope = await recorder.BeginAsync(Ctx(), CancellationToken.None);
        trace.FailCompletes = true;

        var first = await recorder.CompleteAsync(scope, new ExecOutcome(0), CancellationToken.None);

        Assert.Equal(ToolCallStatus.Succeeded, first.Status);
        Assert.True(first.TraceFailed);
        Assert.False(first.Recorded);
        Assert.Equal(ToolCallStatus.Unknown, Assert.Single(trace.Rows).Status);

        trace.FailCompletes = false;
        var retry = await recorder.CompleteAsync(scope, new ExecOutcome(0), CancellationToken.None);

        Assert.True(retry.Recorded);
        Assert.False(retry.TraceFailed);
        Assert.Equal(ToolCallStatus.Succeeded, Assert.Single(trace.Rows).Status);
    }

    [Fact]
    public async Task Cancellation_during_completion_propagates_and_leaves_the_scope_open()
    {
        var (recorder, trace) = Make();
        var scope = await recorder.BeginAsync(Ctx(), CancellationToken.None);
        var failing = new CancellingTrace(trace);
        var cancelling = new ToolCallOutcomeRecorder(new ToolPermissionGate(
            FakeGenericToolPolicy.AllowAll(), trace, new InMemoryAgentFreeze(), new CodingPermissionOptions()), failing);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await cancelling.CompleteAsync(scope, new ExecOutcome(0), CancellationToken.None));
        Assert.False(scope.IsCompleted);

        var done = await recorder.CompleteAsync(scope, new ExecOutcome(0), CancellationToken.None);
        Assert.True(done.Recorded);
    }

    [Fact]
    public async Task The_trace_refuses_a_second_completion_of_a_row()
    {
        var trace = new InMemoryToolCallTrace();
        var row = new ToolCallRecord(Guid.NewGuid(), "s", "t", "Exec", "agent", false, ToolCallStatus.Unknown, null, _hash, DateTimeOffset.UtcNow, null);
        await trace.AppendAsync(row, CancellationToken.None);

        Assert.True(await trace.CompleteAsync(row.Id, ToolCallStatus.Succeeded, 0, CancellationToken.None));
        Assert.False(await trace.CompleteAsync(row.Id, ToolCallStatus.Failed, 1, CancellationToken.None));
        Assert.False(await trace.CompleteAsync(Guid.NewGuid(), ToolCallStatus.Failed, 1, CancellationToken.None));
        Assert.Equal(ToolCallStatus.Succeeded, Assert.Single(trace.Rows).Status);
    }

    [Fact]
    public void The_module_registers_the_recorder()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGenericToolPolicy>(FakeGenericToolPolicy.AllowAll());
        services.AddSingleton<IToolCallTrace>(new InMemoryToolCallTrace());
        services.AddSingleton<IAgentFreeze>(new InMemoryAgentFreeze());
        services.AddBazaarCodingPermissions();

        Assert.IsType<ToolCallOutcomeRecorder>(services.BuildServiceProvider().GetRequiredService<IToolCallRecorder>());
    }

    private sealed class CancellingTrace(IToolCallTrace inner) : IToolCallTrace
    {
        public ValueTask AppendAsync(ToolCallRecord record, CancellationToken cancellationToken) => inner.AppendAsync(record, cancellationToken);

        public ValueTask<bool> CompleteAsync(Guid id, ToolCallStatus status, int? exitCode, CancellationToken cancellationToken) =>
            throw new OperationCanceledException();
    }
}
