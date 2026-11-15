using Bazaar.Server.Agents;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Start;

namespace Bazaar.Server.Tests.Agents.Turns.Start;

/// <summary>Pure tests for <see cref="TurnStarter"/>: a fake peek, depth source and runner, no PostgreSQL.</summary>
public sealed class TurnStarterTests
{
    private static readonly Guid _agent = Guid.CreateVersion7();
    private static readonly Guid _session = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static InboxRow Row(string kind, Guid? gc = null, long? seq = null, string? sourceRef = null, short priority = 4) =>
        new(Guid.CreateVersion7(), kind, priority, gc, seq, sourceRef);

    private static TurnStartRequest Request(int maxItems = 5) =>
        new(_agent, _session, TurnRole.Actor, "holder-1", "route-1", ["tool.a", "tool.b"], maxItems);

    private static TurnStarter Starter(
        IDueInboxReader reader,
        IWakeChainDepthSource depths,
        ITurnRunner runner,
        int unknownRyuseiDepth = 3) =>
        new(reader, depths, runner, new TurnStarterOptions { UnknownRyuseiDepth = unknownRyuseiDepth });

    [Fact]
    public async Task Nothing_due_calls_no_runner_and_reads_no_depth()
    {
        var reader = new FakeDueReader([]);
        var depths = new FakeDepths(_ => 2);
        var runner = new FakeRunner();

        var result = await Starter(reader, depths, runner).StartAsync(Request(), Ct);

        Assert.False(result.Started);
        Assert.Null(result.Outcome);
        Assert.Empty(result.InboxItemIds);
        Assert.Null(result.WakeReason);
        Assert.Equal(0, result.ChainDepth);
        Assert.Empty(runner.Requests);
        Assert.Empty(depths.Calls);
    }

    [Fact]
    public async Task The_wake_reason_is_the_kind_of_the_first_item()
    {
        var reader = new FakeDueReader([Row("mention"), Row("gc_digest")]);
        var runner = new FakeRunner();

        var result = await Starter(reader, new FakeDepths(_ => null), runner).StartAsync(Request(), Ct);

        Assert.Equal("mention", result.WakeReason);
        Assert.Equal("mention", Assert.Single(runner.Requests).WakeReason);
    }

    [Fact]
    public async Task The_chain_depth_is_the_largest_depth_any_item_reports()
    {
        var reader = new FakeDueReader([Row("ryusei_timer", sourceRef: "a"), Row("ryusei_ping", sourceRef: "b")]);
        var depths = new FakeDepths(sourceRef => sourceRef == "a" ? 1 : 2);
        var runner = new FakeRunner();

        var result = await Starter(reader, depths, runner).StartAsync(Request(), Ct);

        Assert.Equal(2, result.ChainDepth);
        Assert.Equal(2, Assert.Single(runner.Requests).ChainDepth);
    }

    [Fact]
    public async Task An_unreadable_ryusei_depth_uses_the_fail_closed_value()
    {
        var reader = new FakeDueReader([Row("ryusei_timer", sourceRef: "not-a-fire-id")]);
        var runner = new FakeRunner();

        var result = await Starter(reader, new FakeDepths(_ => null), runner, unknownRyuseiDepth: 3).StartAsync(Request(), Ct);

        Assert.Equal(3, result.ChainDepth);
        Assert.Equal(3, Assert.Single(runner.Requests).ChainDepth);
    }

    [Fact]
    public async Task The_fail_closed_value_is_configurable_and_still_takes_the_maximum()
    {
        var reader = new FakeDueReader([Row("ryusei_interval", sourceRef: "x"), Row("ryusei_event", sourceRef: "y")]);
        var depths = new FakeDepths(sourceRef => sourceRef == "x" ? 1 : null);
        var runner = new FakeRunner();

        var result = await Starter(reader, depths, runner, unknownRyuseiDepth: 2).StartAsync(Request(), Ct);

        Assert.Equal(2, result.ChainDepth);
    }

    [Fact]
    public async Task A_non_ryusei_item_with_no_depth_contributes_zero()
    {
        var reader = new FakeDueReader([Row("mention", sourceRef: "dm-1"), Row("ryusei_timer", sourceRef: "a")]);
        var depths = new FakeDepths(sourceRef => sourceRef == "a" ? 1 : null);
        var runner = new FakeRunner();

        var result = await Starter(reader, depths, runner).StartAsync(Request(), Ct);

        Assert.Equal(1, result.ChainDepth);
    }

    [Fact]
    public async Task Only_non_ryusei_items_with_no_depth_start_at_zero()
    {
        var reader = new FakeDueReader([Row("mention"), Row("assignment", sourceRef: "card-1")]);
        var runner = new FakeRunner();

        var result = await Starter(reader, new FakeDepths(_ => null), runner, unknownRyuseiDepth: 3).StartAsync(Request(), Ct);

        Assert.True(result.Started);
        Assert.Equal(0, result.ChainDepth);
        Assert.Equal(0, Assert.Single(runner.Requests).ChainDepth);
    }

    [Fact]
    public async Task The_trigger_gc_comes_from_the_first_gc_backed_item()
    {
        var gcA = Guid.CreateVersion7();
        var gcB = Guid.CreateVersion7();
        var reader = new FakeDueReader([Row("mention"), Row("ryusei_ping", gc: gcA, seq: 5), Row("mention", gc: gcB, seq: 9)]);
        var runner = new FakeRunner();

        await Starter(reader, new FakeDepths(_ => null), runner).StartAsync(Request(), Ct);

        var request = Assert.Single(runner.Requests);
        Assert.Equal(gcA.ToString("D"), request.TriggerGcId);
        Assert.Equal(5L, request.TriggerGcSeq);
    }

    [Fact]
    public async Task A_batch_with_no_gc_backed_item_has_no_trigger_gc()
    {
        var reader = new FakeDueReader([Row("ryusei_timer", sourceRef: "a")]);
        var runner = new FakeRunner();

        await Starter(reader, new FakeDepths(_ => 1), runner).StartAsync(Request(), Ct);

        var request = Assert.Single(runner.Requests);
        Assert.Null(request.TriggerGcId);
        Assert.Null(request.TriggerGcSeq);
    }

    [Fact]
    public async Task The_peeked_ids_and_the_request_fields_pass_through()
    {
        var rows = new[] { Row("interrupt", priority: 0), Row("mention", priority: 1), Row("gc_digest", priority: 7) };
        var reader = new FakeDueReader(rows);
        var runner = new FakeRunner();
        var ids = rows.Select(r => r.InboxItemId).ToList();

        var result = await Starter(reader, new FakeDepths(_ => null), runner).StartAsync(Request(maxItems: 5), Ct);

        Assert.Equal([(_agent, 5)], reader.Calls);
        var request = Assert.Single(runner.Requests);
        Assert.Equal(_agent, request.AgentId);
        Assert.Equal(_session, request.SessionId);
        Assert.Equal(TurnRole.Actor, request.Role);
        Assert.Equal("holder-1", request.Holder);
        Assert.Equal("route-1", request.Route);
        Assert.Equal(["tool.a", "tool.b"], request.ToolAllowlist);
        Assert.Equal(ids, request.InboxItemIds);
        Assert.Equal(ids, result.InboxItemIds);
        Assert.Equal("interrupt", result.WakeReason);
    }

    [Fact]
    public async Task The_runner_is_called_once_and_its_outcome_is_returned()
    {
        var reader = new FakeDueReader([Row("scheduled")]);
        var runner = new FakeRunner();

        var result = await Starter(reader, new FakeDepths(_ => null), runner).StartAsync(Request(), Ct);

        Assert.True(result.Started);
        Assert.Same(runner.Result, result.Outcome);
        Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task The_starter_claims_nothing_itself()
    {
        var reader = new FakeDueReader([Row("mention")]);

        await Starter(reader, new FakeDepths(_ => null), new FakeRunner()).StartAsync(Request(), Ct);

        // The fake reader has no claim method; the runner receives ids to claim by. Only the peek was called.
        Assert.Single(reader.Calls);
    }

    [Fact]
    public async Task A_zero_max_items_is_refused()
    {
        var starter = Starter(new FakeDueReader([]), new FakeDepths(_ => null), new FakeRunner());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => starter.StartAsync(Request(maxItems: 0), Ct));
    }

    [Fact]
    public async Task An_empty_holder_is_refused()
    {
        var starter = Starter(new FakeDueReader([]), new FakeDepths(_ => null), new FakeRunner());
        var request = Request() with { Holder = " " };

        await Assert.ThrowsAsync<ArgumentException>(() => starter.StartAsync(request, Ct));
    }

    /// <summary>Returns a fixed batch and records the limit it was asked for.</summary>
    private sealed class FakeDueReader(IReadOnlyList<InboxRow> rows) : IDueInboxReader
    {
        public List<(Guid AgentId, int Limit)> Calls { get; } = [];

        public Task<IReadOnlyList<InboxRow>> PeekDueAsync(Guid agentId, int limit, CancellationToken ct)
        {
            Calls.Add((agentId, limit));
            return Task.FromResult(rows);
        }
    }

    /// <summary>Answers from a function and records every question.</summary>
    private sealed class FakeDepths(Func<string?, int?> answer) : IWakeChainDepthSource
    {
        public List<(string Kind, string? SourceRef)> Calls { get; } = [];

        public Task<int?> ChainDepthAsync(string inboxKind, string? sourceRef, CancellationToken ct)
        {
            Calls.Add((inboxKind, sourceRef));
            return Task.FromResult(answer(sourceRef));
        }
    }

    /// <summary>Records every request and answers with one fixed outcome.</summary>
    private sealed class FakeRunner : ITurnRunner
    {
        public TurnRunOutcome Result { get; } = new(Guid.CreateVersion7(), TurnEndReason.FinalMessage, "done", 1);

        public List<TurnRunRequest> Requests { get; } = [];

        public Task<TurnRunOutcome> RunAsync(TurnRunRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Result);
        }
    }
}
