using Bazaar.Server.Agents;
using Bazaar.Server.Agents.Data;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Agents.Data;

public sealed class TurnStepRepositoryTests(AgentDb db) : IClassFixture<AgentDb>
{
    private TurnRepository Turns => new(db.DataSource);

    private TurnStepRepository Steps => new(db.DataSource);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private async Task<Guid> NewTurnAsync()
    {
        var agent = Guid.CreateVersion7();
        await db.ExecAsync("INSERT INTO agent (agent_id, name, state) VALUES (@id, 'step test', 'active')", ("id", agent));
        return await Turns.StartAsync(new TurnStart(agent, Guid.CreateVersion7(), TurnRole.Actor, "mention"), Ct);
    }

    [Fact]
    public async Task Steps_are_numbered_in_order_and_completed_in_place()
    {
        SkipWithoutDatabase();
        var turn = await NewTurnAsync();

        var first = await Steps.BeginAsync(turn, TurnStepKind.Model, "sha256:aaa", ct: Ct);
        Assert.True(await Steps.CompleteAsync(turn, first, """{"text":"hi"}""", Ct));
        var second = await Steps.BeginAsync(turn, TurnStepKind.Tool, "sha256:bbb", idempotent: true, ct: Ct);

        Assert.Equal(1, first);
        Assert.Equal(2, second);
        var listed = await Steps.ListAsync(turn, Ct);
        Assert.Equal([1, 2], listed.Select(s => s.N));
        Assert.Equal(TurnStepStatus.Completed, listed[0].Status);
        Assert.Contains("hi", listed[0].ResponseJson);
        Assert.Equal(TurnStepStatus.Pending, listed[1].Status);
        Assert.True(listed[1].Idempotent);
        Assert.Null(listed[1].CompletedAt);
    }

    [Fact]
    public async Task A_step_completes_only_once()
    {
        SkipWithoutDatabase();
        var turn = await NewTurnAsync();
        var n = await Steps.BeginAsync(turn, TurnStepKind.Model, "sha256:ccc", ct: Ct);

        Assert.True(await Steps.CompleteAsync(turn, n, "{}", Ct));
        Assert.False(await Steps.CompleteAsync(turn, n, """{"again":true}""", Ct));
    }

    [Fact]
    public async Task An_unfinished_in_flight_tool_is_marked_unknown_and_kept_for_audit()
    {
        SkipWithoutDatabase();
        var turn = await NewTurnAsync();
        var n = await Steps.BeginAsync(turn, TurnStepKind.Tool, "sha256:ddd", idempotent: false, ct: Ct);

        Assert.True(await Steps.MarkUnfinishedAsync(turn, n, TurnStepStatus.Unknown, Ct));

        var step = Assert.Single(await Steps.ListAsync(turn, Ct));
        Assert.Equal(TurnStepStatus.Unknown, step.Status);
        Assert.Null(step.ResponseJson);
        Assert.False(await Steps.CompleteAsync(turn, n, "{}", Ct));
    }

    [Fact]
    public async Task A_re_issued_call_gets_a_new_step_number()
    {
        SkipWithoutDatabase();
        var turn = await NewTurnAsync();
        var lost = await Steps.BeginAsync(turn, TurnStepKind.Model, "sha256:eee", ct: Ct);
        Assert.True(await Steps.MarkUnfinishedAsync(turn, lost, TurnStepStatus.Abandoned, Ct));

        var retry = await Steps.BeginAsync(turn, TurnStepKind.Model, "sha256:eee", ct: Ct);

        Assert.Equal(lost + 1, retry);
    }

    [Fact]
    public async Task Marking_a_completed_step_unfinished_is_refused()
    {
        SkipWithoutDatabase();
        var turn = await NewTurnAsync();
        var n = await Steps.BeginAsync(turn, TurnStepKind.Post, "sha256:fff", ct: Ct);
        Assert.True(await Steps.CompleteAsync(turn, n, "{}", Ct));

        Assert.False(await Steps.MarkUnfinishedAsync(turn, n, TurnStepStatus.Abandoned, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Steps.MarkUnfinishedAsync(turn, n, TurnStepStatus.Completed, Ct));
    }
}
