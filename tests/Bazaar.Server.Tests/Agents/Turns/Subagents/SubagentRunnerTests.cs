using Bazaar.Server.Agents;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Subagents;

namespace Bazaar.Server.Tests.Agents.Turns.Subagents;

public class SubagentRunnerTests
{
    private static readonly Guid _agentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _sessionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly string[] _parentTools = ["gc.read", "vault.read", "vault.write"];
    private static readonly string[] _reorderedChildTools = ["vault.read", "gc.read"];

    private static SubagentRequest Request(
        int parentDepth = 0,
        IReadOnlyList<string>? childTools = null,
        Guid? parentTurnId = null,
        int? parentStepCap = null,
        string? triggerGcId = null,
        long? triggerGcSeq = null) =>
        new(
            AgentId: _agentId,
            SessionId: _sessionId,
            ParentTurnId: parentTurnId ?? Guid.NewGuid(),
            ParentDepth: parentDepth,
            ParentToolAllowlist: _parentTools,
            ParentHolder: "agent-holder",
            Prompt: "Summarise the open threads.",
            ToolAllowlist: childTools ?? ["gc.read"],
            Route: "agent-default",
            ParentStepCap: parentStepCap,
            ParentTriggerGcId: triggerGcId,
            ParentTriggerGcSeq: triggerGcSeq);

    [Fact]
    public async Task A_child_at_depth_three_is_rejected_with_the_default_limit()
    {
        var turns = new ScriptedTurnRunner();
        var runner = new SubagentRunner(turns, new SubagentOptions());

        var error = await Assert.ThrowsAsync<SubagentRejectedException>(() =>
            runner.SpawnAsync(Request(parentDepth: 2), CancellationToken.None));

        Assert.Equal(SubagentRejectionCodes.DepthLimit, error.ReasonCode);
        Assert.Empty(turns.Requests);
    }

    [Fact]
    public async Task A_child_at_the_depth_limit_is_accepted()
    {
        var turns = new ScriptedTurnRunner();
        var runner = new SubagentRunner(turns, new SubagentOptions());

        var result = await runner.SpawnAsync(Request(parentDepth: 1), CancellationToken.None);

        Assert.Equal("done", result.Summary);
        Assert.Equal(2, Assert.Single(turns.Requests).Depth);
    }

    [Fact]
    public async Task A_child_allowlist_with_a_tool_the_parent_lacks_is_rejected()
    {
        var turns = new ScriptedTurnRunner();
        var runner = new SubagentRunner(turns, new SubagentOptions());

        var error = await Assert.ThrowsAsync<SubagentRejectedException>(() =>
            runner.SpawnAsync(Request(childTools: ["gc.read", "shell.exec"]), CancellationToken.None));

        Assert.Equal(SubagentRejectionCodes.ToolNotInParent, error.ReasonCode);
        Assert.Empty(turns.Requests);
    }

    [Fact]
    public async Task A_child_allowlist_that_is_a_subset_of_the_parent_is_accepted()
    {
        var turns = new ScriptedTurnRunner();
        var runner = new SubagentRunner(turns, new SubagentOptions());

        await runner.SpawnAsync(Request(childTools: _reorderedChildTools), CancellationToken.None);

        var child = Assert.Single(turns.Requests);
        Assert.Equal(_reorderedChildTools, child.ToolAllowlist);
    }

    [Fact]
    public async Task The_fourth_concurrent_child_is_rejected_and_a_slot_frees_when_one_finishes()
    {
        var held = new List<TaskCompletionSource<TurnRunOutcome>>();
        var turns = new ScriptedTurnRunner((_, _) =>
        {
            var gate = new TaskCompletionSource<TurnRunOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (held)
            {
                held.Add(gate);
            }

            return gate.Task;
        });
        var runner = new SubagentRunner(turns, new SubagentOptions());
        var parentTurn = Guid.NewGuid();

        var running = new List<Task<SubagentResult>>();
        for (var i = 0; i < 3; i++)
            running.Add(runner.SpawnAsync(Request(parentTurnId: parentTurn), CancellationToken.None));

        var error = await Assert.ThrowsAsync<SubagentRejectedException>(() =>
            runner.SpawnAsync(Request(parentTurnId: parentTurn), CancellationToken.None));
        Assert.Equal(SubagentRejectionCodes.ConcurrencyLimit, error.ReasonCode);
        Assert.Equal(3, turns.Requests.Count);

        // A child of a different parent turn is counted separately.
        var otherParent = runner.SpawnAsync(Request(parentTurnId: Guid.NewGuid()), CancellationToken.None);
        Assert.Equal(4, turns.Requests.Count);

        TaskCompletionSource<TurnRunOutcome> first;
        lock (held)
        {
            first = held[0];
        }

        first.SetResult(new TurnRunOutcome(Guid.NewGuid(), TurnEndReason.FinalMessage, "first", 2));
        await running[0];

        var fifth = runner.SpawnAsync(Request(parentTurnId: parentTurn), CancellationToken.None);
        Assert.Equal(5, turns.Requests.Count);

        // Release everything still held so the test does not leave tasks pending.
        lock (held)
        {
            foreach (var gate in held.Skip(1))
                gate.TrySetResult(new TurnRunOutcome(Guid.NewGuid(), TurnEndReason.FinalMessage, null, 1));
        }

        await Task.WhenAll(running[1], running[2], otherParent, fifth);
    }

    [Fact]
    public async Task A_child_that_fails_still_frees_its_slot()
    {
        var calls = 0;
        var turns = new ScriptedTurnRunner((_, _) =>
        {
            calls++;
            if (calls == 1)
                throw new InvalidOperationException("gateway exploded");
            return Task.FromResult(new TurnRunOutcome(Guid.NewGuid(), TurnEndReason.FinalMessage, "ok", 1));
        });
        var runner = new SubagentRunner(turns, new SubagentOptions { MaxConcurrent = 1 });
        var parentTurn = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.SpawnAsync(Request(parentTurnId: parentTurn), CancellationToken.None));

        var result = await runner.SpawnAsync(Request(parentTurnId: parentTurn), CancellationToken.None);
        Assert.Equal("ok", result.Summary);
    }

    [Fact]
    public async Task The_child_inherits_the_session_and_gets_its_depth_parent_turn_and_a_distinct_holder()
    {
        var turns = new ScriptedTurnRunner();
        var runner = new SubagentRunner(turns, new SubagentOptions());
        var parentTurn = Guid.NewGuid();

        await runner.SpawnAsync(Request(parentDepth: 1, parentTurnId: parentTurn), CancellationToken.None);

        var child = Assert.Single(turns.Requests);
        Assert.Equal(_agentId, child.AgentId);
        Assert.Equal(_sessionId, child.SessionId);
        Assert.Equal(TurnRole.Actor, child.Role);
        Assert.Equal("subagent", child.WakeReason);
        Assert.Empty(child.InboxItemIds);
        Assert.Equal(parentTurn, child.ParentTurnId);
        Assert.Equal(2, child.Depth);
        Assert.Equal("agent-default", child.Route);
        Assert.StartsWith("agent-holder/sub-", child.Holder, StringComparison.Ordinal);
        Assert.NotEqual("agent-holder", child.Holder);
    }

    [Fact]
    public async Task Each_child_gets_its_own_lease_holder()
    {
        var turns = new ScriptedTurnRunner();
        var runner = new SubagentRunner(turns, new SubagentOptions());
        var parentTurn = Guid.NewGuid();

        await runner.SpawnAsync(Request(parentTurnId: parentTurn), CancellationToken.None);
        await runner.SpawnAsync(Request(parentTurnId: parentTurn), CancellationToken.None);

        var holders = turns.Requests.Select(r => r.Holder).ToList();
        Assert.Equal(2, holders.Distinct().Count());
    }

    [Theory]
    [InlineData(30, 40, 30)]
    [InlineData(10, 40, 10)]
    [InlineData(100, 40, 40)]
    [InlineData(null, 40, 40)]
    [InlineData(null, 20, 20)]
    public async Task The_step_cap_is_the_smaller_of_the_parent_cap_and_the_subagent_cap(int? parentCap, int subagentCap, int expected)
    {
        var turns = new ScriptedTurnRunner();
        var runner = new SubagentRunner(turns, new SubagentOptions { StepCap = subagentCap });

        await runner.SpawnAsync(Request(parentStepCap: parentCap), CancellationToken.None);

        Assert.Equal(expected, Assert.Single(turns.Requests).StepCapOverride);
    }

    [Fact]
    public async Task A_child_gets_no_trigger_gc_when_posting_is_off()
    {
        var turns = new ScriptedTurnRunner();
        var runner = new SubagentRunner(turns, new SubagentOptions { MayPost = false });

        await runner.SpawnAsync(Request(triggerGcId: "gc-123", triggerGcSeq: 42), CancellationToken.None);

        var child = Assert.Single(turns.Requests);
        Assert.Null(child.TriggerGcId);
        Assert.Null(child.TriggerGcSeq);
    }

    [Fact]
    public async Task A_child_keeps_the_trigger_gc_when_posting_is_allowed()
    {
        var turns = new ScriptedTurnRunner();
        var runner = new SubagentRunner(turns, new SubagentOptions { MayPost = true });

        await runner.SpawnAsync(Request(triggerGcId: "gc-123", triggerGcSeq: 42), CancellationToken.None);

        var child = Assert.Single(turns.Requests);
        Assert.Equal("gc-123", child.TriggerGcId);
        Assert.Equal(42, child.TriggerGcSeq);
    }

    [Fact]
    public async Task An_abandoned_child_returns_its_outcome_without_throwing()
    {
        var childTurn = Guid.NewGuid();
        var turns = new ScriptedTurnRunner((_, _) =>
            Task.FromResult(new TurnRunOutcome(childTurn, TurnEndReason.LeaseLost, null, 3)));
        var runner = new SubagentRunner(turns, new SubagentOptions());

        var result = await runner.SpawnAsync(Request(), CancellationToken.None);

        Assert.Equal(childTurn, result.ChildTurnId);
        Assert.Equal(TurnEndReason.LeaseLost, result.Reason);
        Assert.Null(result.Summary);
        Assert.Equal(3, result.StepCount);
    }

    [Theory]
    [InlineData(0, 3, 40)]
    [InlineData(2, 0, 40)]
    [InlineData(2, 3, 0)]
    public void Options_below_their_minimum_are_rejected(int maxDepth, int maxConcurrent, int stepCap)
    {
        var options = new SubagentOptions { MaxDepth = maxDepth, MaxConcurrent = maxConcurrent, StepCap = stepCap };

        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new SubagentRunner(new ScriptedTurnRunner(), options));
    }

    [Fact]
    public void The_shipped_defaults_match_the_design_settings()
    {
        var options = new SubagentOptions();

        Assert.Equal(2, options.MaxDepth);
        Assert.Equal(3, options.MaxConcurrent);
        Assert.Equal(40, options.StepCap);
        Assert.False(options.MayPost);
    }
}
