using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Subagents;
using Bazaar.Server.Host.Coding;
using Bazaar.Server.Subagents;
using ModuleRejected = Bazaar.Server.Subagents.SubagentRejectedException;

namespace Bazaar.Server.Host.Tests.Coding;

public sealed class RuntimeSubagentAdapterTests
{
    private static readonly Guid _parentTurn = Guid.NewGuid();
    private static readonly Guid _childTurn = Guid.NewGuid();

    private sealed class FakeTurns(TurnEndReason reason = TurnEndReason.FinalMessage) : ITurnRunner
    {
        public TurnRunRequest? Last { get; private set; }

        public Task<TurnRunOutcome> RunAsync(TurnRunRequest request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(new TurnRunOutcome(_childTurn, reason, "done", 3));
        }
    }

    private sealed class FakeParents(RuntimeSubagentParent? parent) : IRuntimeSubagentParentDirectory
    {
        public ValueTask<RuntimeSubagentParent?> FindAsync(Guid parentTurnId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(parentTurnId == _parentTurn ? parent : null);
    }

    private static RuntimeSubagentParent Parent(params string[] tools) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "holder-1", tools, ParentStepCap: 10);

    private static RuntimeSubagentCall Call(string parentId, int depth = 0, params string[] tools) =>
        new("sub-1", "chain-1", parentId, depth, "review this", new HashSet<string>(tools), "route/x", null, ["ref-1"]);

    private static RuntimeSubagentAdapter Make(FakeTurns turns, RuntimeSubagentParent? parent) =>
        new(new SubagentRunner(turns, new Bazaar.Server.Agents.Turns.Subagents.SubagentOptions()), new FakeParents(parent));

    [Fact]
    public async Task A_completed_child_maps_to_a_completed_result_with_the_child_turn_as_trace_id()
    {
        var turns = new FakeTurns();
        var adapter = Make(turns, Parent("read", "grep"));

        var result = await adapter.RunAsync(Call(_parentTurn.ToString("D"), 0, "read"), CancellationToken.None);

        Assert.Equal(SubagentStatus.Completed, result.Status);
        Assert.Equal("done", result.Summary.Value);
        Assert.Equal(3, result.Usage.Steps);
        Assert.Equal(_childTurn.ToString("D"), result.TraceId);
        Assert.Equal("route/x", turns.Last!.Route);
        Assert.Equal(["read"], turns.Last.ToolAllowlist);
        Assert.Equal(1, turns.Last.Depth);
    }

    [Theory]
    [InlineData(TurnEndReason.Interrupted, SubagentStatus.Cancelled)]
    [InlineData(TurnEndReason.StepCap, SubagentStatus.Failed)]
    [InlineData(TurnEndReason.LeaseLost, SubagentStatus.Failed)]
    public async Task Other_end_reasons_map_to_cancelled_or_failed(TurnEndReason reason, SubagentStatus expected)
    {
        var adapter = Make(new FakeTurns(reason), Parent("read"));

        var result = await adapter.RunAsync(Call(_parentTurn.ToString("D"), 0, "read"), CancellationToken.None);

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task A_runtime_refusal_becomes_a_runtime_limit_rejection()
    {
        var adapter = Make(new FakeTurns(), Parent("read"));

        var depth = await Assert.ThrowsAsync<ModuleRejected>(() =>
            adapter.RunAsync(Call(_parentTurn.ToString("D"), depth: 5, "read"), CancellationToken.None));
        Assert.Equal(SubagentRejection.RuntimeLimit, depth.Reason);

        var tools = await Assert.ThrowsAsync<ModuleRejected>(() =>
            adapter.RunAsync(Call(_parentTurn.ToString("D"), 0, "write"), CancellationToken.None));
        Assert.Equal(SubagentRejection.RuntimeLimit, tools.Reason);
    }

    [Fact]
    public async Task A_parent_id_that_is_not_a_running_turn_is_refused_before_anything_starts()
    {
        var turns = new FakeTurns();
        var adapter = Make(turns, Parent("read"));

        var notGuid = await Assert.ThrowsAsync<ModuleRejected>(() => adapter.RunAsync(Call("session-1", 0, "read"), CancellationToken.None));
        var unknown = await Assert.ThrowsAsync<ModuleRejected>(() =>
            adapter.RunAsync(Call(Guid.NewGuid().ToString("D"), 0, "read"), CancellationToken.None));

        Assert.Equal(SubagentRejection.InvalidSpec, notGuid.Reason);
        Assert.Equal(SubagentRejection.InvalidSpec, unknown.Reason);
        Assert.Null(turns.Last);
    }
}
