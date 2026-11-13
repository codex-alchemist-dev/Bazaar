using Bazaar.Contracts.Gateway;
using Bazaar.Server.Agents;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Tests.Agents.Data;
using Bazaar.Testing.Postgres;
using static Bazaar.Server.Tests.Agents.Turns.ScriptedModelGateway;

namespace Bazaar.Server.Tests.Agents.Turns;

/// <summary>The seam over the production runner: the message builder, the end reasons and the step-cap override, against Postgres.</summary>
public sealed class TurnRunAdapterTests(AgentDb db) : IClassFixture<AgentDb>
{
    private const string _route = "agent-default";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private TurnRunner Runner(ScriptedModelGateway gateway, TurnRunnerOptions? options = null) =>
        new(db.DataSource, gateway, new RecordingToolDispatcher(), options);

    private TurnRepository Turns => new(db.DataSource);

    private static TurnRequest Built(Guid agentId, Func<Guid, CancellationToken, Task<IReadOnlyList<ModelMessage>>> builder) =>
        new(agentId, Guid.CreateVersion7(), "mention", _route, [], null, 0, builder);

    [Fact]
    public async Task The_message_builder_runs_after_the_turn_starts_and_gets_its_id()
    {
        SkipWithoutDatabase();
        var agent = await Agents.CreateAsync("builder", null, Ct);
        Guid? seen = null;
        var gateway = new ScriptedModelGateway().Reply(FinalAnswer("ok"));

        var result = await Runner(gateway).RunAsync(
            Built(agent, (turnId, _) =>
            {
                seen = turnId;
                return Task.FromResult<IReadOnlyList<ModelMessage>>([new ModelMessage(ModelRole.User, "hi")]);
            }),
            Ct);

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        Assert.Equal(result.TurnId, seen);
        Assert.Equal("hi", gateway.Requests[0].Messages[0].Text);
        Assert.Equal(TurnEndReason.FinalMessage, result.EndReason);
    }

    [Fact]
    public async Task A_builder_that_returns_no_messages_fails_the_turn_without_a_model_call()
    {
        SkipWithoutDatabase();
        var agent = await Agents.CreateAsync("empty-builder", null, Ct);
        var gateway = new ScriptedModelGateway();

        var result = await Runner(gateway).RunAsync(
            Built(agent, (_, _) => Task.FromResult<IReadOnlyList<ModelMessage>>([])), Ct);

        Assert.Equal(TurnOutcome.Failed, result.Outcome);
        Assert.Equal(0, gateway.Calls);
        Assert.Null(result.EndReason);
    }

    [Fact]
    public async Task A_step_cap_override_lowers_the_ceiling_for_one_run_and_records_the_step_cap()
    {
        SkipWithoutDatabase();
        var agent = await Agents.CreateAsync("override", null, Ct);
        // Two continuation answers would take two steps; the override allows one, so the second is never called.
        var gateway = new ScriptedModelGateway()
            .Reply(TextEvent("part "), DoneEvent(StopReason.MaxTokens))
            .Reply(TextEvent("more"), DoneEvent(StopReason.MaxTokens));
        var request = new TurnRequest(agent, Guid.CreateVersion7(), "mention", _route,
            [new ModelMessage(ModelRole.User, "go")], null, 0, null, StepCapOverride: 1);

        var result = await Runner(gateway).RunAsync(request, Ct);

        Assert.Equal(TurnOutcome.Failed, result.Outcome);
        Assert.Equal(TurnEndReason.StepCap, result.EndReason);
        Assert.Equal(1, gateway.Calls);
        var turn = await Turns.GetAsync(result.TurnId!.Value, Ct);
        Assert.Equal(TurnStatus.Failed, turn!.Status);
    }

    [Fact]
    public async Task A_step_cap_override_above_the_configured_cap_does_not_raise_it()
    {
        SkipWithoutDatabase();
        var agent = await Agents.CreateAsync("override-high", null, Ct);
        var gateway = new ScriptedModelGateway().Reply(TextEvent("a"), DoneEvent(StopReason.MaxTokens));
        var request = new TurnRequest(agent, Guid.CreateVersion7(), "mention", _route,
            [new ModelMessage(ModelRole.User, "go")], null, 0, null, StepCapOverride: 50);

        var result = await Runner(gateway, new TurnRunnerOptions { StepCap = 1 }).RunAsync(request, Ct);

        Assert.Equal(TurnEndReason.StepCap, result.EndReason);
        Assert.Equal(1, gateway.Calls);
    }

    [Fact]
    public async Task The_adapter_maps_a_completed_run_to_a_final_message_outcome()
    {
        SkipWithoutDatabase();
        var agent = await Agents.CreateAsync("adapter", null, Ct);
        var gateway = new ScriptedModelGateway().Reply(FinalAnswer("done"));
        var adapter = new TurnRunAdapter(Runner(gateway), new StubAssembler(), _route);

        var outcome = await adapter.RunAsync(
            new TurnRunRequest(agent, Guid.CreateVersion7(), TurnRole.Actor, "mention", [], "holder", _route, []), Ct);

        Assert.Equal(TurnEndReason.FinalMessage, outcome.Reason);
        Assert.Equal("done", outcome.Summary);
        Assert.Equal(1, outcome.StepCount);
        Assert.NotEqual(Guid.Empty, outcome.TurnId);
    }

    [Fact]
    public async Task A_request_that_names_inbox_items_claims_exactly_those_and_completes_them()
    {
        SkipWithoutDatabase();
        var agent = await Agents.CreateAsync("adapter-items", null, Ct);
        var first = await EnqueueAsync(agent, "a");
        var second = await EnqueueAsync(agent, "b");
        var untouched = await EnqueueAsync(agent, "c");
        var adapter = new TurnRunAdapter(Runner(new ScriptedModelGateway().Reply(FinalAnswer("ok"))), new StubAssembler(), _route);

        var outcome = await adapter.RunAsync(
            new TurnRunRequest(agent, Guid.CreateVersion7(), TurnRole.Actor, "mention", [first, second], "holder", _route, []), Ct);

        Assert.Equal(TurnEndReason.FinalMessage, outcome.Reason);
        Assert.Equal("done", await StateAsync(first));
        Assert.Equal("done", await StateAsync(second));
        Assert.Equal("pending", await StateAsync(untouched));
    }

    [Fact]
    public async Task The_adapter_offers_the_allowlisted_tools_to_an_actor_turn()
    {
        SkipWithoutDatabase();
        var agent = await Agents.CreateAsync("adapter-tools", null, Ct);
        var gateway = new ScriptedModelGateway().Reply(FinalAnswer("ok"));
        var adapter = new TurnRunAdapter(Runner(gateway), new StubAssembler(), _route, [_read, _write]);

        await adapter.RunAsync(
            new TurnRunRequest(agent, Guid.CreateVersion7(), TurnRole.Actor, "mention", [], "holder", _route, ["write_note"]), Ct);

        Assert.Equal(["write_note"], gateway.Requests[0].Tools.Select(t => t.Name));
    }

    [Fact]
    public async Task The_adapter_offers_no_tools_to_a_reader_turn()
    {
        SkipWithoutDatabase();
        var agent = await Agents.CreateAsync("adapter-reader", null, Ct);
        var gateway = new ScriptedModelGateway().Reply(FinalAnswer("ok"));
        var adapter = new TurnRunAdapter(Runner(gateway), new StubAssembler(), _route, [_read, _write]);

        await adapter.RunAsync(
            new TurnRunRequest(agent, Guid.CreateVersion7(), TurnRole.Reader, "mention", [], "holder", _route, ["read_note", "write_note"]), Ct);

        Assert.Empty(gateway.Requests[0].Tools);
    }

    private static readonly ModelToolDefinition _read = new("read_note", "Reads a note.", """{"type":"object"}""");
    private static readonly ModelToolDefinition _write = new("write_note", "Writes a note.", """{"type":"object"}""");

    /// <summary>Enqueues one pending mention item for <paramref name="agent"/> and returns its id.</summary>
    private async Task<Guid> EnqueueAsync(Guid agent, string tag)
    {
        var sourceRef = $"adapter-{tag}-{Guid.NewGuid():N}";
        await Inbox.EnqueueAsync(new InboxEnqueue(agent, "mention", 1, SourceRef: sourceRef), Ct);
        return await db.ScalarAsync<Guid>(
            "SELECT inbox_item_id FROM inbox_item WHERE agent_id = @id AND source_ref = @ref", ("id", agent), ("ref", sourceRef));
    }

    private Task<string> StateAsync(Guid itemId) =>
        db.ScalarAsync<string>("SELECT state FROM inbox_item WHERE inbox_item_id = @id", ("id", itemId));

    private InboxRepository Inbox => new(db.DataSource);

    private sealed class StubAssembler : ITurnContextAssembler
    {
        public Task<ContextPlan> BuildAsync(TurnContextRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ContextPlan([new ModelMessage(ModelRole.User, "context")], "{}"));
    }

    private AgentRepository Agents => new(db.DataSource);
}
