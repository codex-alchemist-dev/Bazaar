using Bazaar.Server.Agents;
using Bazaar.Server.Agents.LoopGuard;
using Bazaar.Server.Agents.Scheduling;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Start;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Agents.Scheduling;

/// <summary>Pure tests for <see cref="InboxAgentScheduler"/>: fake readers, guard and runner, no PostgreSQL.</summary>
public sealed class InboxAgentSchedulerTests
{
    private readonly FakeDueInboxReader _inbox = new();
    private readonly FakeTurnRunner _runner = new();
    private readonly FakeLoopGuard _guard = new();
    private readonly FakeTimeProvider _time = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AgentSchedulerOptions Options(int maxConcurrentTurns = 4, int maxAgentsPerPass = 20, TimeSpan? backoff = null) => new()
    {
        Enabled = true,
        Route = "route-1",
        HolderPrefix = "wake",
        MaxConcurrentTurns = maxConcurrentTurns,
        MaxAgentsPerPass = maxAgentsPerPass,
        MaxItemsPerTurn = 7,
        ToolAllowlist = ["tool.a"],
        GuardTripBackoff = backoff ?? TimeSpan.FromMinutes(5),
    };

    private InboxAgentScheduler Scheduler(IDueAgentReader due, AgentSchedulerOptions? options = null) => new(
        due,
        new TurnStarter(_inbox, new NoWakeChainDepthSource(), _runner, new TurnStarterOptions()),
        _guard,
        options ?? Options(),
        _time,
        NullLogger<InboxAgentScheduler>.Instance);

    private static Guid[] Agents(int count) => [.. Enumerable.Range(0, count).Select(_ => Guid.CreateVersion7())];

    [Fact]
    public async Task No_due_agents_is_an_empty_pass_that_asks_no_guard_and_starts_no_turn()
    {
        var due = new FakeDueAgentReader();

        var pass = await Scheduler(due).RunOnceAsync(Ct);

        Assert.Equal(AgentSchedulerPass.Empty, pass);
        Assert.Equal([20], due.Limits);
        Assert.Empty(_guard.Wakes);
        Assert.Empty(_runner.Requests);
    }

    [Fact]
    public async Task Each_due_agent_gets_one_turn_built_from_the_options()
    {
        var agents = Agents(3);

        var pass = await Scheduler(new FakeDueAgentReader(agents)).RunOnceAsync(Ct);

        Assert.Equal(new AgentSchedulerPass(3, 3, 0, 0, 0, 0), pass);
        var requests = _runner.Requests;
        Assert.Equal(agents.Order(), requests.Select(r => r.AgentId).Order());
        Assert.All(requests, r =>
        {
            Assert.Equal($"wake:{r.AgentId:D}", r.Holder);
            Assert.Equal("route-1", r.Route);
            Assert.Equal(TurnRole.Actor, r.Role);
            Assert.Equal(["tool.a"], r.ToolAllowlist);
            Assert.Equal(7, r.SessionId.Version);
        });
        Assert.Equal(3, requests.Select(r => r.SessionId).Distinct().Count());
        Assert.All(_guard.Wakes, w => Assert.Equal(WakeKind.Standard, w.Kind));
    }

    [Fact]
    public async Task The_pass_reads_at_most_MaxAgentsPerPass_agents()
    {
        var due = new FakeDueAgentReader(Agents(5));

        var pass = await Scheduler(due, Options(maxAgentsPerPass: 2)).RunOnceAsync(Ct);

        Assert.Equal([2], due.Limits);
        Assert.Equal(2, pass.AgentsDue);
        Assert.Equal(2, pass.TurnsStarted);
    }

    [Fact]
    public async Task A_guard_trip_skips_the_agent_and_starts_no_turn_for_it()
    {
        var agents = Agents(2);
        _guard.Tripped.Add(agents[0]);

        var pass = await Scheduler(new FakeDueAgentReader(agents)).RunOnceAsync(Ct);

        Assert.Equal(new AgentSchedulerPass(2, 1, 1, 0, 0, 0), pass);
        Assert.Equal(agents[1], Assert.Single(_runner.Requests).AgentId);
    }

    [Fact]
    public async Task A_tripped_agent_is_not_asked_about_again_until_the_backoff_has_passed()
    {
        var agent = Guid.CreateVersion7();
        _guard.Tripped.Add(agent);
        var scheduler = Scheduler(new FakeDueAgentReader(agent), Options(backoff: TimeSpan.FromMinutes(5)));

        await scheduler.RunOnceAsync(Ct);
        _guard.Tripped.Clear();
        _time.Advance(TimeSpan.FromMinutes(4));
        var held = await scheduler.RunOnceAsync(Ct);

        Assert.Equal(new AgentSchedulerPass(1, 0, 1, 0, 0, 0), held);
        Assert.Single(_guard.Wakes);
        Assert.Empty(_runner.Requests);

        _time.Advance(TimeSpan.FromMinutes(1));
        var woken = await scheduler.RunOnceAsync(Ct);

        Assert.Equal(new AgentSchedulerPass(1, 1, 0, 0, 0, 0), woken);
        Assert.Equal(2, _guard.Wakes.Count);
        Assert.Single(_runner.Requests);
    }

    [Fact]
    public async Task A_zero_backoff_asks_the_guard_on_every_pass()
    {
        var agent = Guid.CreateVersion7();
        _guard.Tripped.Add(agent);
        var scheduler = Scheduler(new FakeDueAgentReader(agent), Options(backoff: TimeSpan.Zero));

        await scheduler.RunOnceAsync(Ct);
        await scheduler.RunOnceAsync(Ct);

        Assert.Equal(2, _guard.Wakes.Count);
        Assert.Empty(_runner.Requests);
    }

    [Fact]
    public async Task An_agent_with_nothing_due_by_the_time_its_turn_starts_is_counted_as_nothing_due()
    {
        var agents = Agents(2);
        _inbox.NothingDue.Add(agents[1]);

        var pass = await Scheduler(new FakeDueAgentReader(agents)).RunOnceAsync(Ct);

        Assert.Equal(new AgentSchedulerPass(2, 1, 0, 0, 1, 0), pass);
        Assert.Equal(agents[0], Assert.Single(_runner.Requests).AgentId);
    }

    [Fact]
    public async Task A_lost_lease_is_counted_as_a_lease_skip_and_not_as_an_error()
    {
        var agents = Agents(2);
        _runner.OnRun = (request, _) =>
            Task.FromResult(request.AgentId == agents[0] ? TurnEndReason.LeaseLost : TurnEndReason.FinalMessage);

        var pass = await Scheduler(new FakeDueAgentReader(agents)).RunOnceAsync(Ct);

        Assert.Equal(new AgentSchedulerPass(2, 1, 0, 1, 0, 0), pass);
    }

    [Fact]
    public async Task One_agents_exception_is_counted_and_the_other_agents_still_run()
    {
        var agents = Agents(4);
        _runner.OnRun = (request, _) => request.AgentId == agents[1]
            ? throw new InvalidOperationException("boom")
            : Task.FromResult(TurnEndReason.FinalMessage);

        var pass = await Scheduler(new FakeDueAgentReader(agents), Options(maxConcurrentTurns: 1)).RunOnceAsync(Ct);

        Assert.Equal(new AgentSchedulerPass(4, 3, 0, 0, 0, 1), pass);
        Assert.Equal(4, _runner.Requests.Count);
    }

    [Fact]
    public async Task A_turn_that_fails_with_its_own_cancellation_is_an_error_and_does_not_stop_the_pass()
    {
        var agents = Agents(3);
        _runner.OnRun = (request, _) => request.AgentId == agents[0]
            ? throw new OperationCanceledException("a timeout inside the turn")
            : Task.FromResult(TurnEndReason.FinalMessage);

        var pass = await Scheduler(new FakeDueAgentReader(agents), Options(maxConcurrentTurns: 1)).RunOnceAsync(Ct);

        Assert.Equal(new AgentSchedulerPass(3, 2, 0, 0, 0, 1), pass);
    }

    [Fact]
    public async Task Running_turns_never_exceed_MaxConcurrentTurns()
    {
        _runner.OnRun = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(30), ct);
            return TurnEndReason.FinalMessage;
        };

        var pass = await Scheduler(new FakeDueAgentReader(Agents(10)), Options(maxConcurrentTurns: 2)).RunOnceAsync(Ct);

        Assert.Equal(10, pass.TurnsStarted);
        Assert.InRange(_runner.MaxInFlight, 1, 2);
    }

    [Fact]
    public async Task Cancelling_the_pass_cancels_the_running_turns_and_propagates()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.OnRun = async (_, ct) =>
        {
            running.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return TurnEndReason.FinalMessage;
        };

        var pass = Scheduler(new FakeDueAgentReader(Agents(3))).RunOnceAsync(cts.Token);
        await running.Task.WaitAsync(Ct);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pass);
    }

    [Fact]
    public async Task A_token_cancelled_before_the_pass_starts_no_turn()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Scheduler(new FakeDueAgentReader(Agents(2))).RunOnceAsync(cts.Token));
        Assert.Empty(_runner.Requests);
    }

    [Fact]
    public async Task Invalid_options_fail_the_pass()
    {
        var scheduler = Scheduler(new FakeDueAgentReader(Agents(1)), new AgentSchedulerOptions { Enabled = true, Route = " " });

        await Assert.ThrowsAsync<ArgumentException>(() => scheduler.RunOnceAsync(Ct));
        Assert.Empty(_runner.Requests);
    }
}
