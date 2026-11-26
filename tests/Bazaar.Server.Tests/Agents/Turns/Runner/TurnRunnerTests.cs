using System.Globalization;
using System.Text.Json;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Agents;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Agents.LoopGuard;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Posting;
using Bazaar.Server.Agents.Turns.Runner;
using Bazaar.Server.Gateway.Fakes;
using Bazaar.Server.Tests.Agents.Data;
using Bazaar.Testing.Postgres;
using Microsoft.Extensions.Time.Testing;
using TurnRunner = Bazaar.Server.Agents.Turns.Runner.TurnRunner;
using TurnRunnerOptions = Bazaar.Server.Agents.Turns.Runner.TurnRunnerOptions;

namespace Bazaar.Server.Tests.Agents.Turns.Runner;

/// <summary>The turn loop against real Postgres (migration 0010), with the scripted provider behind the gateway seam.</summary>
public sealed class TurnRunnerTests(AgentDb db) : IClassFixture<AgentDb>
{
    private static readonly ModelToolDefinition _lookup = new("lookup", "Looks a thing up.", """{"type":"object"}""");
    private static readonly ModelToolDefinition _send = new("send", "Sends a message.", """{"type":"object"}""");
    private static readonly TimeSpan _lease = TimeSpan.FromMinutes(5);
    private const string _gcId = "gc-test-1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private TurnRepository Turns => new(db.DataSource);
    private TurnStepRepository Steps => new(db.DataSource);
    private LeaseRepository Leases => new(db.DataSource);
    private InboxRepository Inbox => new(db.DataSource);

    private static TurnRunnerOptions Options() => new() { ToolCatalogue = [_lookup, _send] };

    private sealed class Harness(Action? onCall = null)
    {
        public FakeProvider Provider { get; } = new();
        public FakeContextAssembler Assembler { get; } = new();
        public FakeToolDispatcher Tools { get; } = new();
        public FakePostSink Posts { get; } = new();
        public FakeTimeProvider Time { get; } = new(DateTimeOffset.Parse("2026-10-30T12:00:00Z", CultureInfo.InvariantCulture));

        public TurnRunner Create(AgentDb db, TurnRunnerOptions options) => new(
            new LeaseRepository(db.DataSource),
            new InboxRepository(db.DataSource),
            new TurnRepository(db.DataSource),
            new TurnStepRepository(db.DataSource),
            new RunnerGateway(Provider, onCall),
            Assembler,
            Tools,
            Posts,
            Time,
            options);
    }

    private async Task<Guid> NewAgentAsync()
    {
        var id = Guid.CreateVersion7();
        await db.ExecAsync("INSERT INTO agent (agent_id, name, state) VALUES (@id, 'runner test', 'active')", ("id", id));
        return id;
    }

    /// <summary>Enqueues one mention for the agent and returns its inbox item id.</summary>
    private async Task<Guid> EnqueueMentionAsync(Guid agent)
    {
        await Inbox.EnqueueAsync(new InboxEnqueue(agent, "mention", 1, Guid.CreateVersion7(), 1), Ct);
        return await db.ScalarAsync<Guid>("SELECT inbox_item_id FROM inbox_item WHERE agent_id = @id", ("id", agent));
    }

    private static TurnRunRequest Request(Guid agent, IReadOnlyList<Guid>? items = null, string holder = "turn-a", string? gc = _gcId) =>
        new(agent, Guid.CreateVersion7(), TurnRole.Actor, "mention", items ?? [], holder, "test-route", ["lookup"], TriggerGcId: gc);

    private static ScriptStep.Emit Text(string text) => new ScriptStep.Emit(new ModelEvent.TextDelta(text));

    private static ScriptStep.Emit Finish(StopReason reason) => new ScriptStep.Emit(new ModelEvent.Done(reason, "test-model"));

    private static ScriptStep.Emit Usage(long input, long output) =>
        new ScriptStep.Emit(new ModelEvent.UsageReported(new GatewayTokenUsage(input, 0, 0, 0, output, 0)));

    private static ScriptStep[] CallTool(string id, string name, string arguments) =>
    [
        new ScriptStep.Emit(new ModelEvent.ToolCallStart(id, name)),
        new ScriptStep.Emit(new ModelEvent.ToolCallArgsDelta(id, arguments)),
        new ScriptStep.Emit(new ModelEvent.ToolCallEnd(id)),
    ];

    private async Task<int> CountTurnsAsync(Guid agent) =>
        (int)await db.ScalarAsync<long>("SELECT count(*) FROM turn WHERE agent_id = @id", ("id", agent));

    [Fact]
    public async Task A_final_message_completes_the_turn_posts_and_releases_the_lease()
    {
        SkipWithoutDatabase();
        var h = new Harness();
        var agent = await NewAgentAsync();
        var item = await EnqueueMentionAsync(agent);
        h.Provider.EnqueueCall(Text("Done."), Finish(StopReason.EndTurn));

        var outcome = await h.Create(db, Options()).RunAsync(Request(agent, [item]), Ct);

        Assert.Equal(TurnEndReason.FinalMessage, outcome.Reason);
        Assert.Equal("Done.", outcome.Summary);
        Assert.Equal(2, outcome.StepCount);
        var post = Assert.Single(h.Posts.Posts);
        Assert.Equal((agent, outcome.TurnId, _gcId, "Done."), (post.AgentId, post.TurnId, post.GcId, post.Text));
        var turn = await Turns.GetAsync(outcome.TurnId, Ct);
        Assert.Equal(TurnStatus.Completed, turn!.Status);
        Assert.Contains("\"system\"", turn.ContextManifestJson);
        var steps = await Steps.ListAsync(outcome.TurnId, Ct);
        Assert.Equal(new[] { TurnStepKind.Model, TurnStepKind.Post }, steps.Select(s => s.Kind).ToArray());
        Assert.All(steps, s => Assert.Equal(TurnStepStatus.Completed, s.Status));
        Assert.Null(await Leases.GetAsync(agent, Ct));
        Assert.Equal(1, await Inbox.CountAsync(agent, InboxState.Done, Ct));
    }

    [Fact]
    public async Task A_post_refused_by_a_loop_guard_ends_the_turn_as_post_refused_and_releases_the_items()
    {
        SkipWithoutDatabase();
        var h = new Harness();
        h.Posts.ThrowOnPost = new AgentPostRefusedException(GuardKind.PingPong, "Two agents alternated.");
        var agent = await NewAgentAsync();
        var item = await EnqueueMentionAsync(agent);
        h.Provider.EnqueueCall(Text("Done."), Finish(StopReason.EndTurn));

        var outcome = await h.Create(db, Options()).RunAsync(Request(agent, [item]), Ct);

        Assert.Equal(TurnEndReason.PostRefused, outcome.Reason);
        Assert.Equal("Done.", outcome.Summary);
        Assert.Empty(h.Posts.Posts);
        var steps = await Steps.ListAsync(outcome.TurnId, Ct);
        Assert.Equal(new[] { TurnStepKind.Model, TurnStepKind.Post }, steps.Select(s => s.Kind).ToArray());
        Assert.Equal(new[] { TurnStepStatus.Completed, TurnStepStatus.Abandoned }, steps.Select(s => s.Status).ToArray());
        Assert.Equal(TurnStatus.Failed, (await Turns.GetAsync(outcome.TurnId, Ct))!.Status);
        Assert.Equal(1, await Inbox.CountAsync(agent, InboxState.Pending, Ct));
        Assert.Null(await Leases.GetAsync(agent, Ct));
    }

    [Fact]
    public async Task A_tool_call_is_dispatched_and_the_model_is_called_again_with_the_result()
    {
        SkipWithoutDatabase();
        var h = new Harness();
        var agent = await NewAgentAsync();
        var item = await EnqueueMentionAsync(agent);
        h.Provider.EnqueueCall([.. CallTool("c1", "lookup", """{"q":"x"}"""), Finish(StopReason.ToolUse)]);
        h.Provider.EnqueueCall(Text("Found it."), Finish(StopReason.EndTurn));
        var options = Options() with { IdempotentTools = ["lookup"] };

        var outcome = await h.Create(db, options).RunAsync(Request(agent, [item], gc: null), Ct);

        Assert.Equal(TurnEndReason.FinalMessage, outcome.Reason);
        Assert.Equal("Found it.", outcome.Summary);
        Assert.Equal(3, outcome.StepCount);
        Assert.Empty(h.Posts.Posts);
        var dispatch = Assert.Single(h.Tools.Requests);
        Assert.Equal("c1", dispatch.Call.Id);
        Assert.True(dispatch.Idempotent);
        Assert.Equal(2, h.Provider.Calls.Count);
        // The allowlist names only lookup, so the catalogue's send tool is not offered.
        Assert.Equal(["lookup"], h.Provider.Calls[0].Tools.Select(t => t.Name).ToArray());
        Assert.Contains(h.Provider.Calls[1].Messages, m => m.Role == ModelRole.Tool && m.ToolCallId == "c1" && m.Text == """{"ok":true}""");
    }

    [Fact]
    public async Task The_step_cap_stops_the_turn_before_the_next_model_call()
    {
        SkipWithoutDatabase();
        var h = new Harness();
        var agent = await NewAgentAsync();
        var item = await EnqueueMentionAsync(agent);
        h.Provider.EnqueueCall([.. CallTool("c1", "lookup", "{}"), Finish(StopReason.ToolUse)]);

        var outcome = await h.Create(db, Options() with { StepCap = 2 }).RunAsync(Request(agent, [item]), Ct);

        Assert.Equal(TurnEndReason.StepCap, outcome.Reason);
        Assert.Equal(2, outcome.StepCount);
        Assert.Single(h.Provider.Calls);
        Assert.Single(h.Tools.Requests);
        Assert.Equal(TurnStatus.Failed, (await Turns.GetAsync(outcome.TurnId, Ct))!.Status);
        Assert.Equal(1, await Inbox.CountAsync(agent, InboxState.Pending, Ct));
    }

    [Fact]
    public async Task The_token_ceiling_stops_the_turn_before_tools_run()
    {
        SkipWithoutDatabase();
        var h = new Harness();
        var agent = await NewAgentAsync();
        var item = await EnqueueMentionAsync(agent);
        h.Provider.EnqueueCall([Usage(150_000, 60_000), .. CallTool("c1", "lookup", "{}"), Finish(StopReason.ToolUse)]);

        var outcome = await h.Create(db, Options()).RunAsync(Request(agent, [item]), Ct);

        Assert.Equal(TurnEndReason.TokenCeiling, outcome.Reason);
        Assert.Single(h.Provider.Calls);
        Assert.Empty(h.Tools.Requests);
    }

    [Fact]
    public async Task The_wall_clock_stops_the_turn_on_the_injected_clock()
    {
        SkipWithoutDatabase();
        var h = new Harness();
        var agent = await NewAgentAsync();
        var item = await EnqueueMentionAsync(agent);
        h.Provider.EnqueueCall([.. CallTool("c1", "lookup", "{}"), Finish(StopReason.ToolUse)]);
        h.Tools.Handler = (_, _) =>
        {
            h.Time.Advance(TimeSpan.FromMinutes(11));
            return Task.FromResult(new ToolDispatchResult(ToolDispatchOutcome.Executed, "{}"));
        };

        var outcome = await h.Create(db, Options()).RunAsync(Request(agent, [item]), Ct);

        Assert.Equal(TurnEndReason.WallClock, outcome.Reason);
        Assert.Single(h.Provider.Calls);
    }

    [Fact]
    public async Task A_lease_held_by_another_run_returns_lease_lost_and_leaves_items_pending()
    {
        SkipWithoutDatabase();
        var h = new Harness();
        var agent = await NewAgentAsync();
        var item = await EnqueueMentionAsync(agent);
        Assert.True(await Leases.TryAcquireAsync(agent, "someone-else", _lease, Ct));

        var outcome = await h.Create(db, Options()).RunAsync(Request(agent, [item]), Ct);

        Assert.Equal(TurnEndReason.LeaseLost, outcome.Reason);
        Assert.Equal(Guid.Empty, outcome.TurnId);
        Assert.Equal(0, outcome.StepCount);
        Assert.Empty(h.Provider.Calls);
        Assert.Equal(0, await CountTurnsAsync(agent));
        Assert.Equal(1, await Inbox.CountAsync(agent, InboxState.Pending, Ct));
        Assert.Equal("someone-else", (await Leases.GetAsync(agent, Ct))!.Holder);
    }

    [Fact]
    public async Task A_lease_stolen_mid_turn_ends_the_turn_as_lease_lost_at_the_next_step()
    {
        SkipWithoutDatabase();
        var h = new Harness();
        var agent = await NewAgentAsync();
        var item = await EnqueueMentionAsync(agent);
        h.Provider.EnqueueCall([.. CallTool("c1", "lookup", "{}"), Finish(StopReason.ToolUse)]);
        h.Provider.EnqueueCall(Text("not reached"), Finish(StopReason.EndTurn));
        h.Tools.Handler = async (_, ct) =>
        {
            // Another holder takes the lease while this one is out of time: the next renewal fails.
            await Leases.ReleaseAsync(agent, "turn-a", ct);
            await Leases.TryAcquireAsync(agent, "thief", _lease, ct);
            return new ToolDispatchResult(ToolDispatchOutcome.Executed, "{}");
        };

        var outcome = await h.Create(db, Options()).RunAsync(Request(agent, [item]), Ct);

        Assert.Equal(TurnEndReason.LeaseLost, outcome.Reason);
        Assert.Single(h.Provider.Calls);
        Assert.Equal(TurnStatus.Failed, (await Turns.GetAsync(outcome.TurnId, Ct))!.Status);
        Assert.Equal("thief", (await Leases.GetAsync(agent, Ct))!.Holder);
        Assert.Equal(1, await Inbox.CountAsync(agent, InboxState.Pending, Ct));
    }

    [Fact]
    public async Task A_failed_model_call_ends_the_turn_as_gateway_failed_and_abandons_the_step()
    {
        SkipWithoutDatabase();
        var h = new Harness();
        var agent = await NewAgentAsync();
        var item = await EnqueueMentionAsync(agent);
        h.Provider.EnqueueCall(Text("partial"), new ScriptStep.Emit(new ModelEvent.Failure("provider_error", "upstream broke")));

        var outcome = await h.Create(db, Options()).RunAsync(Request(agent, [item]), Ct);

        Assert.Equal(TurnEndReason.GatewayFailed, outcome.Reason);
        Assert.Equal(1, outcome.StepCount);
        Assert.Empty(h.Posts.Posts);
        var step = Assert.Single(await Steps.ListAsync(outcome.TurnId, Ct));
        Assert.Equal(TurnStepKind.Model, step.Kind);
        Assert.Equal(TurnStepStatus.Abandoned, step.Status);
        Assert.Equal(TurnStatus.Failed, (await Turns.GetAsync(outcome.TurnId, Ct))!.Status);
        Assert.Equal(1, await Inbox.CountAsync(agent, InboxState.Pending, Ct));
    }

    [Fact]
    public async Task A_gateway_that_throws_ends_the_turn_as_gateway_failed()
    {
        SkipWithoutDatabase();
        var h = new Harness();
        var agent = await NewAgentAsync();
        var item = await EnqueueMentionAsync(agent);
        // No script is queued, so the fake provider throws when the call starts.

        var outcome = await h.Create(db, Options()).RunAsync(Request(agent, [item]), Ct);

        Assert.Equal(TurnEndReason.GatewayFailed, outcome.Reason);
        Assert.Equal(TurnStepStatus.Abandoned, (await Steps.ListAsync(outcome.TurnId, Ct)).Single().Status);
    }

    [Fact]
    public async Task Cancellation_during_a_model_call_interrupts_the_turn_and_abandons_the_step()
    {
        SkipWithoutDatabase();
        using var cts = new CancellationTokenSource();
        var h = new Harness(onCall: () => cts.Cancel());
        var agent = await NewAgentAsync();
        var item = await EnqueueMentionAsync(agent);
        h.Provider.EnqueueCall(Text("never"), Finish(StopReason.EndTurn));

        var outcome = await h.Create(db, Options()).RunAsync(Request(agent, [item]), cts.Token);

        Assert.Equal(TurnEndReason.Interrupted, outcome.Reason);
        Assert.Equal(1, outcome.StepCount);
        Assert.Equal(TurnStepStatus.Abandoned, (await Steps.ListAsync(outcome.TurnId, Ct)).Single().Status);
        Assert.Equal(TurnStatus.Failed, (await Turns.GetAsync(outcome.TurnId, Ct))!.Status);
        Assert.Equal(1, await Inbox.CountAsync(agent, InboxState.Pending, Ct));
        Assert.Null(await Leases.GetAsync(agent, Ct));
    }

    [Fact]
    public async Task Resume_reuses_a_completed_model_step_without_a_second_gateway_call()
    {
        SkipWithoutDatabase();
        var h = new Harness();
        var agent = await NewAgentAsync();
        var item = await EnqueueMentionAsync(agent);
        var turn = await StartRunningTurnAsync(agent);
        await SaveCompletedModelStepAsync(turn, new ModelToolCall("c1", "lookup", "{}"));
        h.Provider.EnqueueCall(Text("Resumed."), Finish(StopReason.EndTurn));

        var outcome = await h.Create(db, Options()).ResumeAsync(turn, Request(agent, [item]), Ct);

        Assert.Equal(TurnEndReason.FinalMessage, outcome.Reason);
        Assert.Equal(turn, outcome.TurnId);
        // Only the step after the completed one reached the gateway, and the tool from the saved response ran once.
        var call = Assert.Single(h.Provider.Calls);
        Assert.Contains(call.Messages, m => m.Role == ModelRole.Assistant && m.ToolCalls is { Count: 1 } && m.ToolCalls[0].Id == "c1");
        Assert.Single(h.Tools.Requests);
        Assert.Equal(TurnStatus.Completed, (await Turns.GetAsync(turn, Ct))!.Status);
    }

    [Fact]
    public async Task Resume_reuses_a_completed_tool_step_and_does_not_dispatch_it_again()
    {
        SkipWithoutDatabase();
        var h = new Harness();
        var agent = await NewAgentAsync();
        var item = await EnqueueMentionAsync(agent);
        var turn = await StartRunningTurnAsync(agent);
        var call = new ModelToolCall("c1", "lookup", "{}");
        await SaveCompletedModelStepAsync(turn, call);
        var tool = await Steps.BeginAsync(turn, TurnStepKind.Tool, TurnStepHash.ToolCall(1, call), idempotent: true, Ct);
        await Steps.CompleteAsync(
            turn, tool, JsonSerializer.Serialize(new ToolStepPayload("c1", ToolDispatchOutcome.Executed, """{"ok":1}""", null), TurnRunnerJson.Default.ToolStepPayload), Ct);
        h.Provider.EnqueueCall(Text("Used it."), Finish(StopReason.EndTurn));

        var outcome = await h.Create(db, Options()).ResumeAsync(turn, Request(agent, [item]), Ct);

        Assert.Equal(TurnEndReason.FinalMessage, outcome.Reason);
        Assert.Empty(h.Tools.Requests);
        var model = Assert.Single(h.Provider.Calls);
        Assert.Contains(model.Messages, m => m.Role == ModelRole.Tool && m.ToolCallId == "c1" && m.Text == """{"ok":1}""");
    }

    [Fact]
    public async Task An_in_flight_non_idempotent_tool_is_recorded_unknown_and_the_model_is_told()
    {
        SkipWithoutDatabase();
        var h = new Harness();
        var agent = await NewAgentAsync();
        var item = await EnqueueMentionAsync(agent);
        var turn = await StartRunningTurnAsync(agent);
        var call = new ModelToolCall("c1", "send", "{}");
        await SaveCompletedModelStepAsync(turn, call);
        // The tool step was pending when the run died: it is not idempotent, so it must not run again.
        await Steps.BeginAsync(turn, TurnStepKind.Tool, TurnStepHash.ToolCall(1, call), idempotent: false, Ct);
        h.Provider.EnqueueCall(Text("Noted."), Finish(StopReason.EndTurn));

        var outcome = await h.Create(db, Options()).ResumeAsync(turn, Request(agent, [item]), Ct);

        Assert.Equal(TurnEndReason.FinalMessage, outcome.Reason);
        Assert.Empty(h.Tools.Requests);
        var model = Assert.Single(h.Provider.Calls);
        Assert.Contains(model.Messages, m => m.Role == ModelRole.Tool && m.ToolCallId == "c1" && m.Text.Contains("in flight", StringComparison.Ordinal));
        var toolStep = (await Steps.ListAsync(turn, Ct)).Single(s => s.Kind == TurnStepKind.Tool);
        Assert.Equal(TurnStepStatus.Unknown, toolStep.Status);
    }

    [Fact]
    public async Task Resume_rejects_a_turn_that_is_not_running_and_releases_the_claims()
    {
        SkipWithoutDatabase();
        var h = new Harness();
        var agent = await NewAgentAsync();
        var item = await EnqueueMentionAsync(agent);
        var turn = await StartRunningTurnAsync(agent);
        Assert.True(await Turns.EndAsync(turn, TurnStatus.Failed, Ct));

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Create(db, Options()).ResumeAsync(turn, Request(agent, [item]), Ct));

        Assert.Null(await Leases.GetAsync(agent, Ct));
        Assert.Equal(1, await Inbox.CountAsync(agent, InboxState.Pending, Ct));
        Assert.Empty(h.Provider.Calls);
    }

    private async Task<Guid> StartRunningTurnAsync(Guid agent) =>
        await Turns.StartAsync(new TurnStart(agent, Guid.CreateVersion7(), TurnRole.Actor, "mention"), Ct);

    /// <summary>Writes a model step that completed with one tool call, as an earlier run would have.</summary>
    private async Task SaveCompletedModelStepAsync(Guid turn, ModelToolCall call)
    {
        var payload = new ModelStepPayload("", [call], new GatewayTokenUsage(10, 0, 0, 0, 5, 0), StopReason.ToolUse, "test-model");
        var n = await Steps.BeginAsync(turn, TurnStepKind.Model, TurnStepHash.ModelRequest([], []), false, Ct);
        await Steps.CompleteAsync(turn, n, JsonSerializer.Serialize(payload, TurnRunnerJson.Default.ModelStepPayload), Ct);
    }
}
