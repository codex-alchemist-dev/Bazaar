using System.Text.Json;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Agents;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Tests.Agents.Data;
using Bazaar.Testing.Postgres;
using static Bazaar.Server.Tests.Agents.Turns.ScriptedModelGateway;

namespace Bazaar.Server.Tests.Agents.Turns;

/// <summary>
/// Tool dispatch in the production turn runner, against PostgreSQL (doc 06, "Tool dispatch" and "Concurrency and recovery").
/// These are CI-only: they skip where no test PostgreSQL is available, as the other turn tests do. The decision rules they
/// rely on are checked without a database in <see cref="ToolStepPlannerTests"/>.
/// </summary>
public sealed class TurnToolDispatchTests(AgentDb db) : IClassFixture<AgentDb>
{
    private const string _route = "agent-default";
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private AgentRepository Agents => new(db.DataSource);

    private TurnRepository Turns => new(db.DataSource);

    private TurnStepRepository Steps => new(db.DataSource);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private TurnRunner Runner(ScriptedModelGateway gateway, RecordingToolDispatcher dispatcher, TurnRunnerOptions? options = null) =>
        new(db.DataSource, gateway, dispatcher, options);

    private static TurnRequest Request(Guid agentId) =>
        new(agentId, Guid.CreateVersion7(), "mention", _route, [new ModelMessage(ModelRole.User, "hello")]);

    /// <summary>The events of one model response that asks for the given tool calls, in order.</summary>
    private static ModelEvent[] ToolCalls(params (string Id, string Name, string Arguments)[] calls)
    {
        var events = new List<ModelEvent>();
        foreach (var (id, name, arguments) in calls)
        {
            events.Add(new ModelEvent.ToolCallStart(id, name));
            events.Add(new ModelEvent.ToolCallArgsDelta(id, arguments));
            events.Add(new ModelEvent.ToolCallEnd(id));
        }

        events.Add(DoneEvent(StopReason.ToolUse));
        return events.ToArray();
    }

    /// <summary>The stored response of a model step that asked for one tool call.</summary>
    private static string ModelStepJson(ModelStepToolCall call) =>
        JsonSerializer.Serialize(new ModelStepResponse(string.Empty, "ToolUse", "fake-model", [call], 3), _json);

    private static ModelStepToolCall ReadFile() => new("call-1", "read_file", """{"path":"notes.md"}""");

    /// <summary>
    /// A turn of the agent that finished its model call (asking for <see cref="ReadFile"/>) and then crashed, leaving the tool step
    /// in <paramref name="toolStatus"/>. The turn is marked abandoned, as recovery does. Returns the tool step's request hash.
    /// </summary>
    private async Task<string> AbandonedToolTurnAsync(
        Guid agentId, TurnStepStatus toolStatus, bool idempotent, string? toolResponseJson = null)
    {
        var modelHash = ModelRequestHash.Compute(_route, [new ModelMessage(ModelRole.User, "hello")], []);
        var call = ReadFile();
        var turn = await Turns.StartAsync(new TurnStart(agentId, Guid.CreateVersion7(), TurnRole.Actor, "mention"), Ct);
        var modelStep = await Steps.BeginAsync(turn, TurnStepKind.Model, modelHash, ct: Ct);
        await Steps.CompleteAsync(turn, modelStep, ModelStepJson(call), Ct);

        var toolHash = ToolStepPlanner.Hash(modelHash, call);
        var toolStep = await Steps.BeginAsync(turn, TurnStepKind.Tool, toolHash, idempotent, Ct);
        if (toolStatus == TurnStepStatus.Completed)
            await Steps.CompleteAsync(turn, toolStep, toolResponseJson!, Ct);
        else
            await Steps.MarkUnfinishedAsync(turn, toolStep, toolStatus, Ct);

        await db.ExecAsync("UPDATE turn SET status = 'abandoned', ended_at = now() WHERE turn_id = @id", ("id", turn));
        return toolHash;
    }

    [Fact]
    public async Task A_requested_tool_runs_through_the_dispatcher_and_its_result_reaches_the_model_on_the_next_step()
    {
        SkipWithoutDatabase();
        var agent = await Agents.CreateAsync("tool-run", null, Ct);
        var gateway = new ScriptedModelGateway()
            .Reply(ToolCalls(("call-1", "read_file", """{"path":"notes.md"}""")))
            .Reply(FinalAnswer("Read it."));
        var dispatcher = new RecordingToolDispatcher(_ => new ToolDispatchResult(ToolDispatchOutcome.Executed, """{"notes":"hi"}"""));

        var result = await Runner(gateway, dispatcher).RunAsync(Request(agent), Ct);

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        Assert.Equal("Read it.", result.FinalText);
        Assert.Equal(2, gateway.Calls);

        // The call reached the dispatcher once, with the caller it was verified for.
        var dispatched = Assert.Single(dispatcher.Requests);
        Assert.Equal(agent, dispatched.AgentId);
        Assert.Equal(result.TurnId, dispatched.TurnId);
        Assert.Equal("read_file", dispatched.Call.Name);
        Assert.Equal("call-1", dispatched.Call.Id);
        Assert.False(dispatched.Idempotent);

        // The second model step sees the assistant's call and the tool result, matched by call id.
        var second = gateway.Requests[1].Messages;
        Assert.Contains(second, m => m.Role == ModelRole.Assistant && m.ToolCalls is { Count: 1 } && m.ToolCalls[0].Id == "call-1");
        Assert.Contains(second, m => m.Role == ModelRole.Tool && m.ToolCallId == "call-1" && m.Text == """{"notes":"hi"}""");

        // Each step is stored in order: the model, the tool, then the model that answered.
        var steps = await Steps.ListAsync(result.TurnId!.Value, Ct);
        Assert.Equal([TurnStepKind.Model, TurnStepKind.Tool, TurnStepKind.Model], steps.Select(s => s.Kind));
        Assert.All(steps, s => Assert.Equal(TurnStepStatus.Completed, s.Status));
        var stored = JsonSerializer.Deserialize<ToolStepResponse>(steps[1].ResponseJson!, _json);
        Assert.Equal(new ToolStepResponse("call-1", "read_file", ToolDispatchOutcome.Executed, """{"notes":"hi"}""", null), stored);
    }

    [Fact]
    public async Task Calls_in_one_response_run_in_order_and_a_refused_call_is_told_it_did_not_run()
    {
        SkipWithoutDatabase();
        var agent = await Agents.CreateAsync("tool-refused", null, Ct);
        var gateway = new ScriptedModelGateway()
            .Reply(ToolCalls(("call-1", "read_file", "{}"), ("call-2", "write_file", "{}")))
            .Reply(FinalAnswer("Understood."));
        var dispatcher = new RecordingToolDispatcher(r => r.Call.Id == "call-1"
            ? new ToolDispatchResult(ToolDispatchOutcome.Denied, Reason: "RequiresGrant")
            : new ToolDispatchResult(ToolDispatchOutcome.PendingApproval, Reason: "RequireApproval"));

        var result = await Runner(gateway, dispatcher).RunAsync(Request(agent), Ct);

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        Assert.Equal(["call-1", "call-2"], dispatcher.Requests.Select(r => r.Call.Id));
        var second = gateway.Requests[1].Messages;
        Assert.Contains(second, m => m.Role == ModelRole.Tool && m.ToolCallId == "call-1" && m.Text.Contains("did not run"));
        Assert.Contains(second, m => m.Role == ModelRole.Tool && m.ToolCallId == "call-2" && m.Text.Contains("needs owner approval"));
    }

    [Fact]
    public async Task A_completed_tool_step_of_an_abandoned_turn_is_reused_and_the_tool_does_not_run_again()
    {
        SkipWithoutDatabase();
        var agent = await Agents.CreateAsync("tool-reuse", null, Ct);
        var stored = JsonSerializer.Serialize(
            new ToolStepResponse("call-1", "read_file", ToolDispatchOutcome.Executed, """{"notes":"kept"}""", null), _json);
        await AbandonedToolTurnAsync(agent, TurnStepStatus.Completed, idempotent: false, toolResponseJson: stored);

        // The model step is reused too, so the only gateway call is the one that answers after the tool result.
        var gateway = new ScriptedModelGateway().Reply(FinalAnswer("Used the note."));
        var dispatcher = new RecordingToolDispatcher();
        var result = await Runner(gateway, dispatcher).RunAsync(Request(agent), Ct);

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        Assert.Equal("Used the note.", result.FinalText);
        Assert.Empty(dispatcher.Requests);
        Assert.Equal(1, gateway.Calls);
        Assert.Contains(gateway.Requests[0].Messages, m => m.Role == ModelRole.Tool && m.Text == """{"notes":"kept"}""");
    }

    [Fact]
    public async Task An_unknown_tool_step_is_not_run_again_and_the_model_is_told_the_outcome_is_unknown()
    {
        SkipWithoutDatabase();
        var agent = await Agents.CreateAsync("tool-unknown", null, Ct);
        await AbandonedToolTurnAsync(agent, TurnStepStatus.Unknown, idempotent: false);

        var gateway = new ScriptedModelGateway().Reply(FinalAnswer("Noted."));
        var dispatcher = new RecordingToolDispatcher();
        var result = await Runner(gateway, dispatcher).RunAsync(Request(agent), Ct);

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        Assert.Empty(dispatcher.Requests);
        Assert.Contains(gateway.Requests[0].Messages, m => m.Role == ModelRole.Tool && m.Text == ToolStepPlanner.UnknownResult);
    }

    [Fact]
    public async Task An_abandoned_idempotent_tool_step_runs_again_when_the_tool_is_marked_idempotent()
    {
        SkipWithoutDatabase();
        var agent = await Agents.CreateAsync("tool-idempotent", null, Ct);
        await AbandonedToolTurnAsync(agent, TurnStepStatus.Abandoned, idempotent: true);

        var gateway = new ScriptedModelGateway()
            .Reply(FinalAnswer("Read again."));
        var dispatcher = new RecordingToolDispatcher();
        var options = new TurnRunnerOptions { IdempotentTools = ["read_file"] };

        var result = await Runner(gateway, dispatcher, options).RunAsync(Request(agent), Ct);

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        var dispatched = Assert.Single(dispatcher.Requests);
        Assert.Equal("read_file", dispatched.Call.Name);
        Assert.True(dispatched.Idempotent);
    }

    [Fact]
    public async Task The_step_cap_counts_tool_steps_with_the_model_steps()
    {
        SkipWithoutDatabase();
        var agent = await Agents.CreateAsync("tool-cap", null, Ct);
        var gateway = new ScriptedModelGateway()
            .Reply(ToolCalls(("call-1", "read_file", "{}")))
            .Reply(FinalAnswer("Never reached."));
        var dispatcher = new RecordingToolDispatcher();

        // One model step and one tool step use the whole cap, so the next model step is refused.
        var result = await Runner(gateway, dispatcher, new TurnRunnerOptions { StepCap = 2 }).RunAsync(Request(agent), Ct);

        Assert.Equal(TurnOutcome.Failed, result.Outcome);
        Assert.Equal(TurnEndReason.StepCap, result.EndReason);
        Assert.Equal(1, gateway.Calls);
        Assert.Single(dispatcher.Requests);
    }

    [Fact]
    public async Task The_wake_chain_depth_of_the_turn_reaches_the_dispatcher_with_each_call()
    {
        SkipWithoutDatabase();
        var agent = await Agents.CreateAsync("tool-depth", null, Ct);
        var gateway = new ScriptedModelGateway()
            .Reply(ToolCalls(("call-1", "read_file", "{}")))
            .Reply(FinalAnswer("Done."));
        var dispatcher = new RecordingToolDispatcher();
        var request = new TurnRequest(agent, Guid.CreateVersion7(), "mention", _route, [new ModelMessage(ModelRole.User, "hello")], ChainDepth: 3);

        await Runner(gateway, dispatcher).RunAsync(request, Ct);

        Assert.Equal(3, Assert.Single(dispatcher.Requests).ChainDepth);
    }

    [Fact]
    public async Task A_dispatcher_failure_fails_the_turn_and_leaves_a_non_idempotent_call_unknown()
    {
        SkipWithoutDatabase();
        var agent = await Agents.CreateAsync("tool-broken", null, Ct);
        var gateway = new ScriptedModelGateway().Reply(ToolCalls(("call-1", "read_file", "{}")));
        var dispatcher = new RecordingToolDispatcher(_ => throw new InvalidOperationException("audit store is down"));

        var result = await Runner(gateway, dispatcher).RunAsync(Request(agent), Ct);

        Assert.Equal(TurnOutcome.Failed, result.Outcome);
        Assert.Equal("turn error (InvalidOperationException)", result.FailureReason);
        var tool = Assert.Single(await Steps.ListAsync(result.TurnId!.Value, Ct), s => s.Kind == TurnStepKind.Tool);
        Assert.Equal(TurnStepStatus.Unknown, tool.Status);
    }
}
