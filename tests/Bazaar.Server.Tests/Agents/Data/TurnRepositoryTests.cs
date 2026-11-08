using Bazaar.Server.Agents;
using Bazaar.Server.Agents.Data;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Agents.Data;

public sealed class TurnRepositoryTests(AgentDb db) : IClassFixture<AgentDb>
{
    private TurnRepository Turns => new(db.DataSource);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private async Task<Guid> NewAgentAsync()
    {
        var id = Guid.CreateVersion7();
        await db.ExecAsync("INSERT INTO agent (agent_id, name, state) VALUES (@id, 'turn test', 'active')", ("id", id));
        return id;
    }

    [Fact]
    public async Task A_turn_starts_running_and_ends_once()
    {
        SkipWithoutDatabase();
        var agent = await NewAgentAsync();
        var session = Guid.CreateVersion7();

        var turn = await Turns.StartAsync(new TurnStart(agent, session, TurnRole.Actor, "mention", ManifestJson: """{"memory":["n1"]}"""), Ct);
        var started = await Turns.GetAsync(turn, Ct);

        Assert.NotNull(started);
        Assert.Equal(TurnStatus.Running, started.Status);
        Assert.Equal(session, started.SessionId);
        Assert.Null(started.EndedAt);
        Assert.Contains("n1", started.ContextManifestJson);

        Assert.True(await Turns.EndAsync(turn, TurnStatus.Completed, Ct));
        Assert.False(await Turns.EndAsync(turn, TurnStatus.Failed, Ct));
        var ended = await Turns.GetAsync(turn, Ct);
        Assert.Equal(TurnStatus.Completed, ended!.Status);
        Assert.NotNull(ended.EndedAt);
    }

    [Fact]
    public async Task Ending_a_turn_as_running_is_refused()
    {
        SkipWithoutDatabase();
        var agent = await NewAgentAsync();
        var turn = await Turns.StartAsync(new TurnStart(agent, Guid.CreateVersion7(), TurnRole.Reader, "digest"), Ct);

        await Assert.ThrowsAsync<ArgumentException>(() => Turns.EndAsync(turn, TurnStatus.Running, Ct));
    }

    [Fact]
    public async Task A_child_turn_needs_a_parent_and_a_depth_above_zero()
    {
        SkipWithoutDatabase();
        var agent = await NewAgentAsync();
        var session = Guid.CreateVersion7();
        var parent = await Turns.StartAsync(new TurnStart(agent, session, TurnRole.Actor, "assignment"), Ct);

        await Assert.ThrowsAsync<ArgumentException>(() => Turns.StartAsync(new TurnStart(agent, session, TurnRole.Actor, "assignment", parent, 0), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Turns.StartAsync(new TurnStart(agent, session, TurnRole.Actor, "assignment", null, 1), Ct));

        var child = await Turns.StartAsync(new TurnStart(agent, session, TurnRole.Actor, "assignment", parent, 1), Ct);
        Assert.Equal(parent, (await Turns.GetAsync(child, Ct))!.ParentTurnId);
    }

    [Fact]
    public async Task Running_turns_are_listed_per_agent()
    {
        SkipWithoutDatabase();
        var agent = await NewAgentAsync();
        var done = await Turns.StartAsync(new TurnStart(agent, Guid.CreateVersion7(), TurnRole.Actor, "mention"), Ct);
        await Turns.EndAsync(done, TurnStatus.Completed, Ct);
        var open = await Turns.StartAsync(new TurnStart(agent, Guid.CreateVersion7(), TurnRole.Actor, "mention"), Ct);

        var running = await Turns.ListRunningAsync(agent, Ct);

        Assert.Equal([open], running.Select(t => t.TurnId));
    }

    [Fact]
    public async Task A_database_check_refuses_an_unknown_wake_reason_shape()
    {
        SkipWithoutDatabase();
        var agent = await NewAgentAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => Turns.StartAsync(new TurnStart(agent, Guid.CreateVersion7(), TurnRole.Actor, "Bad Reason"), Ct));
    }
}
