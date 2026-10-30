using Bazaar.Server.Agents;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Agents.Recovery;
using Bazaar.Server.Tests.Agents.Data;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Agents.Recovery;

public sealed class AgentLeaseRecoveryTests(AgentDb db) : IClassFixture<AgentDb>
{
    private static readonly TimeSpan _lease = TimeSpan.FromMinutes(5);

    private AgentLeaseRecovery Recovery => new(db.DataSource);

    private AgentRepository Agents => new(db.DataSource);

    private TurnRepository Turns => new(db.DataSource);

    private TurnStepRepository Steps => new(db.DataSource);

    private LeaseRepository Leases => new(db.DataSource);

    private InboxRepository Inbox => new(db.DataSource);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    /// <summary>Makes a lease expire in the database clock, without waiting.</summary>
    private Task ExpireAsync(Guid agentId) =>
        db.ExecAsync("UPDATE agent_lease SET expires_at = now() - interval '1 second' WHERE agent_id = @id", ("id", agentId));

    /// <summary>Recovers whatever earlier tests left expired, so each test counts only its own rows.</summary>
    private async Task DrainAsync() => await Recovery.RecoverExpiredAsync(Ct);

    /// <summary>An agent holding a live lease with one running turn.</summary>
    private async Task<(Guid Agent, Guid Turn)> HeldTurnAsync(string holder)
    {
        var agent = await Agents.CreateAsync("recovery", null, Ct);
        Assert.True(await Leases.TryAcquireAsync(agent, holder, _lease, Ct));
        var turn = await Turns.StartAsync(new TurnStart(agent, Guid.CreateVersion7(), TurnRole.Actor, "mention"), Ct);
        return (agent, turn);
    }

    [Fact]
    public async Task An_expired_lease_abandons_the_turn_and_marks_each_unfinished_step_by_kind()
    {
        SkipWithoutDatabase();
        await DrainAsync();
        var (agent, turn) = await HeldTurnAsync("crashed-1");
        var done = await Steps.BeginAsync(turn, TurnStepKind.Model, "sha256:m1", ct: Ct);
        await Steps.CompleteAsync(turn, done, "{}", Ct);
        var model = await Steps.BeginAsync(turn, TurnStepKind.Model, "sha256:m2", ct: Ct);
        var idempotentTool = await Steps.BeginAsync(turn, TurnStepKind.Tool, "sha256:t1", idempotent: true, ct: Ct);
        var sideEffectTool = await Steps.BeginAsync(turn, TurnStepKind.Tool, "sha256:t2", idempotent: false, ct: Ct);
        var post = await Steps.BeginAsync(turn, TurnStepKind.Post, "sha256:p1", ct: Ct);
        await Inbox.EnqueueAsync(new InboxEnqueue(agent, "mention", 1, SourceRef: "dm-1"), Ct);
        await Inbox.ClaimAsync(agent, "crashed-1", 5, Ct);
        await ExpireAsync(agent);

        var report = await Recovery.RecoverExpiredAsync(Ct);

        Assert.Equal(new LeaseRecoveryReport(ExpiredLeases: 1, TurnsAbandoned: 1, StepsAbandoned: 2, StepsUnknown: 2, ItemsReleased: 1), report);
        var listed = await Steps.ListAsync(turn, Ct);
        Assert.Equal(TurnStepStatus.Completed, listed.Single(s => s.N == done).Status);
        Assert.Equal(TurnStepStatus.Abandoned, listed.Single(s => s.N == model).Status);
        Assert.Equal(TurnStepStatus.Abandoned, listed.Single(s => s.N == idempotentTool).Status);
        Assert.Equal(TurnStepStatus.Unknown, listed.Single(s => s.N == sideEffectTool).Status);
        Assert.Equal(TurnStepStatus.Unknown, listed.Single(s => s.N == post).Status);
        Assert.Equal(TurnStatus.Abandoned, (await Turns.GetAsync(turn, Ct))!.Status);
        Assert.Null(await Leases.GetAsync(agent, Ct));
        Assert.Equal(1, await Inbox.CountAsync(agent, InboxState.Pending, Ct));
        Assert.Equal(0, await Inbox.CountAsync(agent, InboxState.Claimed, Ct));
    }

    [Fact]
    public async Task A_live_lease_is_left_alone()
    {
        SkipWithoutDatabase();
        await DrainAsync();
        var (agent, turn) = await HeldTurnAsync("live-1");
        var step = await Steps.BeginAsync(turn, TurnStepKind.Model, "sha256:live", ct: Ct);

        var report = await Recovery.RecoverExpiredAsync(Ct);

        Assert.Equal(0, report.ExpiredLeases);
        Assert.Equal(TurnStatus.Running, (await Turns.GetAsync(turn, Ct))!.Status);
        Assert.Equal(TurnStepStatus.Pending, (await Steps.ListAsync(turn, Ct)).Single(s => s.N == step).Status);
        Assert.NotNull(await Leases.GetAsync(agent, Ct));
    }

    [Fact]
    public async Task A_second_pass_finds_nothing_new()
    {
        SkipWithoutDatabase();
        await DrainAsync();
        var (agent, _) = await HeldTurnAsync("twice-1");
        await ExpireAsync(agent);
        await Recovery.RecoverExpiredAsync(Ct);

        var again = await Recovery.RecoverExpiredAsync(Ct);

        Assert.Equal(new LeaseRecoveryReport(0, 0, 0, 0, 0), again);
    }

    [Fact]
    public async Task After_recovery_the_agent_can_lease_again_and_a_retry_takes_a_new_step_number()
    {
        SkipWithoutDatabase();
        await DrainAsync();
        var (agent, turn) = await HeldTurnAsync("resume-1");
        var lost = await Steps.BeginAsync(turn, TurnStepKind.Model, "sha256:retry", ct: Ct);
        await ExpireAsync(agent);
        await Recovery.RecoverExpiredAsync(Ct);

        Assert.True(await Leases.TryAcquireAsync(agent, "resume-2", _lease, Ct));
        var next = await Turns.StartAsync(new TurnStart(agent, Guid.CreateVersion7(), TurnRole.Actor, "mention"), Ct);
        var retry = await Steps.BeginAsync(turn, TurnStepKind.Model, "sha256:retry", ct: Ct);

        Assert.Equal(TurnStatus.Running, (await Turns.GetAsync(next, Ct))!.Status);
        Assert.Equal(lost + 1, retry);
    }

    [Fact]
    public async Task Every_expired_agent_is_recovered_in_one_pass()
    {
        SkipWithoutDatabase();
        await DrainAsync();
        var agents = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var (agent, _) = await HeldTurnAsync($"batch-{i}");
            await ExpireAsync(agent);
            agents.Add(agent);
        }

        var report = await Recovery.RecoverExpiredAsync(Ct);

        Assert.Equal(3, report.ExpiredLeases);
        Assert.Equal(3, report.TurnsAbandoned);
        foreach (var agent in agents)
            Assert.Null(await Leases.GetAsync(agent, Ct));
    }

    [Fact]
    public async Task Concurrent_passes_recover_each_lease_once()
    {
        SkipWithoutDatabase();
        await DrainAsync();
        for (var i = 0; i < 2; i++)
        {
            var (agent, _) = await HeldTurnAsync($"race-{i}");
            await ExpireAsync(agent);
        }

        var reports = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Recovery.RecoverExpiredAsync(Ct)));

        Assert.Equal(2, reports.Sum(r => r.ExpiredLeases));
        Assert.Equal(2, reports.Sum(r => r.TurnsAbandoned));
    }
}
