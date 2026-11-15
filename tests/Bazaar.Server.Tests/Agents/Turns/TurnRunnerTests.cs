using Bazaar.Contracts.Gateway;
using Bazaar.Server.Agents;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Agents.Recovery;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Tests.Agents.Data;
using Bazaar.Testing.Postgres;
using static Bazaar.Server.Tests.Agents.Turns.ScriptedModelGateway;

namespace Bazaar.Server.Tests.Agents.Turns;

public sealed class TurnRunnerTests(AgentDb db) : IClassFixture<AgentDb>
{
    private static readonly TimeSpan _lease = TimeSpan.FromMinutes(5);
    private const string _route = "agent-default";

    private AgentRepository Agents => new(db.DataSource);

    private TurnRepository Turns => new(db.DataSource);

    private TurnStepRepository Steps => new(db.DataSource);

    private LeaseRepository Leases => new(db.DataSource);

    private InboxRepository Inbox => new(db.DataSource);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private TurnRunner Runner(ScriptedModelGateway gateway, TurnRunnerOptions? options = null) =>
        new(db.DataSource, gateway, new RecordingToolDispatcher(), options);

    private static TurnRequest Request(Guid agentId, int claim = 0, string text = "hello") =>
        new(agentId, Guid.CreateVersion7(), "mention", _route, [new ModelMessage(ModelRole.User, text)], null, claim);

    /// <summary>An agent with <paramref name="items"/> pending mention items.</summary>
    private async Task<Guid> AgentWithItemsAsync(int items)
    {
        var agent = await Agents.CreateAsync("runner", null, Ct);
        for (var i = 0; i < items; i++)
            await Inbox.EnqueueAsync(new InboxEnqueue(agent, "mention", 1, SourceRef: $"dm-{Guid.NewGuid():N}"), Ct);
        return agent;
    }

    /// <summary>Makes a lease expire in the database clock, without waiting.</summary>
    private Task ExpireAsync(Guid agentId) =>
        db.ExecAsync("UPDATE agent_lease SET expires_at = now() - interval '1 second' WHERE agent_id = @id", ("id", agentId));

    private Task<int> CountTurnsAsync(Guid agentId, string status) =>
        db.ScalarAsync<int>("SELECT count(*)::int FROM turn WHERE agent_id = @id AND status = @status", ("id", agentId), ("status", status));

    [Fact]
    public async Task A_final_message_completes_the_turn_marks_items_done_and_releases_the_lease()
    {
        SkipWithoutDatabase();
        var agent = await AgentWithItemsAsync(2);
        var gateway = new ScriptedModelGateway().Reply(TextEvent("Hel"), TextEvent("lo"), UsageEvent(10, 5), DoneEvent(StopReason.EndTurn));

        var result = await Runner(gateway).RunAsync(Request(agent, claim: 5), Ct);

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        Assert.Equal("Hello", result.FinalText);
        Assert.Equal(2, result.ClaimedItems);
        Assert.Equal(1, gateway.Calls);

        var turn = await Turns.GetAsync(result.TurnId!.Value, Ct);
        Assert.NotNull(turn);
        Assert.Equal(TurnStatus.Completed, turn.Status);
        Assert.NotNull(turn.EndedAt);
        Assert.Equal(2, await Inbox.CountAsync(agent, InboxState.Done, Ct));
        Assert.Equal(0, await Inbox.CountAsync(agent, InboxState.Claimed, Ct));
        Assert.Null(await Leases.GetAsync(agent, Ct));

        var steps = await Steps.ListAsync(turn.TurnId, Ct);
        var step = Assert.Single(steps);
        Assert.Equal(TurnStepKind.Model, step.Kind);
        Assert.Equal(TurnStepStatus.Completed, step.Status);
        Assert.Contains("Hello", step.ResponseJson);
    }

    [Fact]
    public async Task A_max_tokens_stop_asks_for_the_rest_and_joins_the_text()
    {
        SkipWithoutDatabase();
        var agent = await AgentWithItemsAsync(0);
        var gateway = new ScriptedModelGateway()
            .Reply(TextEvent("Part one. "), DoneEvent(StopReason.MaxTokens))
            .Reply(TextEvent("Part two."), DoneEvent(StopReason.EndTurn));

        var result = await Runner(gateway).RunAsync(Request(agent), Ct);

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        Assert.Equal("Part one. Part two.", result.FinalText);
        Assert.Equal(2, gateway.Calls);
        // The second request carries the cut answer and the continue prompt after the original message.
        Assert.Equal(3, gateway.Requests[1].Messages.Count);
        Assert.Equal(ModelRole.Assistant, gateway.Requests[1].Messages[1].Role);
    }

    [Fact]
    public async Task The_step_cap_ends_the_turn_as_failed_and_returns_the_claims()
    {
        SkipWithoutDatabase();
        var agent = await AgentWithItemsAsync(1);
        var gateway = new ScriptedModelGateway()
            .Reply(TextEvent("a"), DoneEvent(StopReason.MaxTokens))
            .Reply(TextEvent("b"), DoneEvent(StopReason.MaxTokens))
            .Reply(TextEvent("c"), DoneEvent(StopReason.MaxTokens));

        var result = await Runner(gateway, new TurnRunnerOptions { StepCap = 3 }).RunAsync(Request(agent, claim: 1), Ct);

        Assert.Equal(TurnOutcome.Failed, result.Outcome);
        Assert.Contains("step cap", result.FailureReason);
        Assert.Equal(3, gateway.Calls);
        Assert.Equal(3, result.ModelSteps);
        Assert.Equal(1, await CountTurnsAsync(agent, "failed"));
        Assert.Equal(1, await Inbox.CountAsync(agent, InboxState.Pending, Ct));
        Assert.Null(await Leases.GetAsync(agent, Ct));
    }

    [Fact]
    public async Task The_token_ceiling_ends_the_turn_as_failed()
    {
        SkipWithoutDatabase();
        var agent = await AgentWithItemsAsync(0);
        var gateway = new ScriptedModelGateway().Reply(TextEvent("x"), UsageEvent(80, 40), DoneEvent(StopReason.MaxTokens));

        var result = await Runner(gateway, new TurnRunnerOptions { TokenCeiling = 100 }).RunAsync(Request(agent), Ct);

        Assert.Equal(TurnOutcome.Failed, result.Outcome);
        Assert.Contains("token ceiling", result.FailureReason);
        Assert.Equal(1, gateway.Calls);
        Assert.Equal(1, await CountTurnsAsync(agent, "failed"));
        Assert.Null(await Leases.GetAsync(agent, Ct));
    }

    [Fact]
    public async Task The_wall_clock_cancels_a_model_call_that_runs_too_long()
    {
        SkipWithoutDatabase();
        var agent = await AgentWithItemsAsync(1);
        var gateway = new ScriptedModelGateway().Hold();
        var options = new TurnRunnerOptions { WallClock = TimeSpan.FromMilliseconds(200), LeaseDuration = TimeSpan.FromSeconds(30) };

        var result = await Runner(gateway, options).RunAsync(Request(agent, claim: 1), Ct);

        Assert.Equal(TurnOutcome.Failed, result.Outcome);
        Assert.Contains("wall clock", result.FailureReason);
        Assert.Equal(1, gateway.Calls);
        Assert.Equal(1, await CountTurnsAsync(agent, "failed"));
        Assert.Equal(1, await Inbox.CountAsync(agent, InboxState.Pending, Ct));
        Assert.Null(await Leases.GetAsync(agent, Ct));
    }

    [Fact]
    public async Task A_failure_event_abandons_the_pending_step_and_releases_the_claims()
    {
        SkipWithoutDatabase();
        var agent = await AgentWithItemsAsync(1);
        var gateway = new ScriptedModelGateway().Reply(TextEvent("partial"), new ModelEvent.Failure("rate_limited", "slow down", 30));

        var result = await Runner(gateway).RunAsync(Request(agent, claim: 1), Ct);

        Assert.Equal(TurnOutcome.Failed, result.Outcome);
        Assert.Equal("model call failed (rate_limited)", result.FailureReason);
        Assert.Equal(1, await Inbox.CountAsync(agent, InboxState.Pending, Ct));
        Assert.Null(await Leases.GetAsync(agent, Ct));

        // The call did not finish, so its step is abandoned, not completed: recovery must not reuse a partial answer.
        var step = Assert.Single(await Steps.ListAsync(result.TurnId!.Value, Ct));
        Assert.Equal(TurnStepStatus.Abandoned, step.Status);
    }

    [Fact]
    public async Task A_gateway_exception_releases_the_claims_and_the_lease()
    {
        SkipWithoutDatabase();
        var agent = await AgentWithItemsAsync(2);
        var gateway = new ScriptedModelGateway().Throw(new IOException("connection reset"));

        var result = await Runner(gateway).RunAsync(Request(agent, claim: 5), Ct);

        Assert.Equal(TurnOutcome.Failed, result.Outcome);
        Assert.Equal("turn error (IOException)", result.FailureReason);
        Assert.Equal(1, await CountTurnsAsync(agent, "failed"));
        Assert.Equal(2, await Inbox.CountAsync(agent, InboxState.Pending, Ct));
        Assert.Equal(0, await Inbox.CountAsync(agent, InboxState.Claimed, Ct));
        Assert.Null(await Leases.GetAsync(agent, Ct));
    }

    [Fact]
    public async Task A_cancelled_run_fails_its_turn_releases_the_claims_and_lease_and_rethrows()
    {
        SkipWithoutDatabase();
        var agent = await AgentWithItemsAsync(1);
        var gateway = new ScriptedModelGateway().Hold();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = Runner(gateway).RunAsync(Request(agent, claim: 1), cancel.Token);
        await gateway.Held.WaitAsync(Ct);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(1, await CountTurnsAsync(agent, "failed"));
        Assert.Equal(1, await Inbox.CountAsync(agent, InboxState.Pending, Ct));
        Assert.Null(await Leases.GetAsync(agent, Ct));
    }

    [Fact]
    public async Task A_held_lease_returns_NotAcquired_without_a_model_call_or_writes()
    {
        SkipWithoutDatabase();
        var agent = await AgentWithItemsAsync(2);
        Assert.True(await Leases.TryAcquireAsync(agent, "someone-else", _lease, Ct));
        var gateway = new ScriptedModelGateway();

        var result = await Runner(gateway).RunAsync(Request(agent, claim: 5), Ct);

        Assert.Equal(TurnOutcome.NotAcquired, result.Outcome);
        Assert.Null(result.TurnId);
        Assert.Equal(0, gateway.Calls);
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT count(*)::int FROM turn WHERE agent_id = @id", ("id", agent)));
        Assert.Equal(2, await Inbox.CountAsync(agent, InboxState.Pending, Ct));
        // The other holder's lease is still there.
        Assert.Equal("someone-else", (await Leases.GetAsync(agent, Ct))!.Holder);
    }

    [Fact]
    public async Task A_recovered_turn_reuses_its_completed_model_step_without_a_gateway_call()
    {
        SkipWithoutDatabase();
        await Recovery().RecoverExpiredAsync(Ct);
        var agent = await AgentWithItemsAsync(1);
        var messages = new List<ModelMessage> { new(ModelRole.User, "hello") };
        var hash = ModelRequestHash.Compute(_route, messages, []);

        // A turn that finished its model call and then crashed before acting on the answer.
        Assert.True(await Leases.TryAcquireAsync(agent, "crashed-1", _lease, Ct));
        var crashed = await Turns.StartAsync(new TurnStart(agent, Guid.CreateVersion7(), TurnRole.Actor, "mention"), Ct);
        var n = await Steps.BeginAsync(crashed, TurnStepKind.Model, hash, ct: Ct);
        await Steps.CompleteAsync(
            crashed,
            n,
            """{"text":"Recovered answer","stopReason":"EndTurn","servedModel":"fake-model","toolCalls":[],"tokens":15}""",
            Ct);
        Assert.Single(await Inbox.ClaimAsync(agent, "crashed-1", 1, Ct));
        await ExpireAsync(agent);
        var report = await Recovery().RecoverExpiredAsync(Ct);
        Assert.True(report.TurnsAbandoned >= 1);
        Assert.Equal(TurnStatus.Abandoned, (await Turns.GetAsync(crashed, Ct))!.Status);

        var gateway = new ScriptedModelGateway();
        var result = await Runner(gateway).RunAsync(
            new TurnRequest(agent, Guid.CreateVersion7(), "mention", _route, messages, null, InboxClaimLimit: 1), Ct);

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        Assert.Equal("Recovered answer", result.FinalText);
        Assert.Equal(0, gateway.Calls);
        Assert.Equal(1, result.ReusedSteps);
        Assert.Equal(1, result.ModelSteps);
        Assert.Equal(1, await Inbox.CountAsync(agent, InboxState.Done, Ct));
        var reusedStep = Assert.Single(await Steps.ListAsync(result.TurnId!.Value, Ct));
        Assert.Equal(TurnStepStatus.Completed, reusedStep.Status);
        Assert.Contains("Recovered answer", reusedStep.ResponseJson);
        Assert.Null(await Leases.GetAsync(agent, Ct));
    }

    private AgentLeaseRecovery Recovery() => new(db.DataSource);
}

public sealed class TurnRunnerOptionsTests
{
    [Fact]
    public void A_lease_that_does_not_outlive_the_wall_clock_is_refused()
    {
        var options = new TurnRunnerOptions { WallClock = TimeSpan.FromMinutes(5), LeaseDuration = TimeSpan.FromMinutes(5) };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void The_step_cap_must_allow_at_least_one_step()
    {
        Assert.Throws<ArgumentOutOfRangeException>(new TurnRunnerOptions { StepCap = 0 }.Validate);
    }

    [Fact]
    public void The_defaults_are_valid()
    {
        new TurnRunnerOptions().Validate();
    }

    [Fact]
    public void A_blank_idempotent_tool_name_is_refused()
    {
        Assert.Throws<ArgumentException>(new TurnRunnerOptions { IdempotentTools = [" "] }.Validate);
    }

    [Fact]
    public void No_tool_is_idempotent_by_default()
    {
        Assert.Empty(new TurnRunnerOptions().IdempotentTools);
    }
}
