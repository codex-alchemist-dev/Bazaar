using Bazaar.Contracts.Gateway;
using Bazaar.Server.Agents;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Presence.Sources.Abstractions;
using Bazaar.Server.Tests.Agents.Data;
using Bazaar.Testing.Postgres;
using static Bazaar.Server.Tests.Agents.Turns.ScriptedModelGateway;

namespace Bazaar.Server.Tests.Agents.Turns;

/// <summary>The production turn runner reports its lifecycle to the presence port, against PostgreSQL (doc 06, docs/design/23).</summary>
public sealed class TurnRunnerPresenceTests(AgentDb db) : IClassFixture<AgentDb>
{
    private const string _route = "agent-default";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private TurnRunner Runner(ScriptedModelGateway gateway, RecordingToolDispatcher dispatcher, TurnActivityReporterTests.FakeSink sink) =>
        new(db.DataSource, gateway, dispatcher, activity: new TurnActivityReporter(sink));

    private static TurnRequest Request(Guid agentId) =>
        new(agentId, Guid.CreateVersion7(), "mention", _route, [new ModelMessage(ModelRole.User, "hello")]);

    private static ModelEvent[] ToolCall(string id, string name) =>
    [
        new ModelEvent.ToolCallStart(id, name),
        new ModelEvent.ToolCallArgsDelta(id, "{}"),
        new ModelEvent.ToolCallEnd(id),
        DoneEvent(StopReason.ToolUse),
    ];

    [Fact]
    public async Task A_turn_with_a_tool_reports_start_model_tool_model_and_end_in_order()
    {
        SkipWithoutDatabase();
        var agent = await new AgentRepository(db.DataSource).CreateAsync("presence-run", null, Ct);
        var gateway = new ScriptedModelGateway().Reply(ToolCall("c1", "read_file")).Reply(FinalAnswer("Done."));
        var sink = new TurnActivityReporterTests.FakeSink();

        var result = await Runner(gateway, new RecordingToolDispatcher(), sink).RunAsync(Request(agent), Ct);

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        Assert.Equal(
            [
                TurnActivityKind.TurnStarted,
                TurnActivityKind.ModelCallStarted,
                TurnActivityKind.ToolStarted,
                TurnActivityKind.ToolFinished,
                TurnActivityKind.ModelCallStarted,
                TurnActivityKind.TurnEnded,
            ],
            sink.Activities.Select(a => a.Kind));
        Assert.All(sink.Activities, a => Assert.Equal((agent, result.TurnId!.Value), (a.AgentId, a.TurnId)));
        Assert.Equal("read_file", sink.Activities.First(a => a.Kind == TurnActivityKind.ToolStarted).ToolName);
    }

    [Fact]
    public async Task A_tool_that_needs_approval_starts_an_approval_wait_that_ends_when_the_turn_goes_on()
    {
        SkipWithoutDatabase();
        var agent = await new AgentRepository(db.DataSource).CreateAsync("presence-approval", null, Ct);
        var gateway = new ScriptedModelGateway().Reply(ToolCall("c1", "send_mail")).Reply(FinalAnswer("Waiting on the owner."));
        var dispatcher = new RecordingToolDispatcher(_ => new ToolDispatchResult(ToolDispatchOutcome.PendingApproval, null, "high impact"));
        var sink = new TurnActivityReporterTests.FakeSink();

        await Runner(gateway, dispatcher, sink).RunAsync(Request(agent), Ct);

        var kinds = sink.Activities.Select(a => a.Kind).ToList();
        var started = kinds.IndexOf(TurnActivityKind.ApprovalWaitStarted);
        var ended = kinds.IndexOf(TurnActivityKind.ApprovalWaitEnded);
        Assert.InRange(started, 0, kinds.Count);
        Assert.True(ended > started);
        Assert.Equal(TurnActivityKind.ModelCallStarted, kinds[ended + 1]);
        Assert.Equal(TurnActivityKind.TurnEnded, kinds[^1]);
    }

    [Fact]
    public async Task A_throwing_sink_does_not_fail_the_turn()
    {
        SkipWithoutDatabase();
        var agent = await new AgentRepository(db.DataSource).CreateAsync("presence-throw", null, Ct);
        var gateway = new ScriptedModelGateway().Reply(FinalAnswer("Hello."));
        var sink = new TurnActivityReporterTests.FakeSink(_ => new InvalidOperationException("sink down"));

        var result = await Runner(gateway, new RecordingToolDispatcher(), sink).RunAsync(Request(agent), Ct);

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        Assert.Equal(TurnActivityKind.TurnEnded, sink.Activities[^1].Kind);
    }
}
