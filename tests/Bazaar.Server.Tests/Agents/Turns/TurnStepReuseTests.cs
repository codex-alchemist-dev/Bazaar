using Bazaar.Server.Agents;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Tests.Agents.Data;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Agents.Turns;

/// <summary>The reuse lookup behind recovery: a completed model step from an abandoned turn of the same agent (doc 06).</summary>
public sealed class TurnStepReuseTests(AgentDb db) : IClassFixture<AgentDb>
{
    private const string _response = """{"text":"kept","stopReason":"EndTurn","servedModel":"fake-model","toolCalls":[],"tokens":3}""";

    private AgentRepository Agents => new(db.DataSource);

    private TurnRepository Turns => new(db.DataSource);

    private TurnStepRepository Steps => new(db.DataSource);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    /// <summary>A turn for the agent with one completed model step carrying <see cref="_response"/>.</summary>
    private async Task<Guid> TurnWithCompletedStepAsync(Guid agentId, string hash)
    {
        var turn = await Turns.StartAsync(new TurnStart(agentId, Guid.CreateVersion7(), TurnRole.Actor, "mention"), Ct);
        var n = await Steps.BeginAsync(turn, TurnStepKind.Model, hash, ct: Ct);
        await Steps.CompleteAsync(turn, n, _response, Ct);
        return turn;
    }

    private Task AbandonAsync(Guid turnId) =>
        db.ExecAsync("UPDATE turn SET status = 'abandoned', ended_at = now() WHERE turn_id = @id", ("id", turnId));

    [Fact]
    public async Task A_completed_step_in_an_abandoned_turn_is_found_by_its_request_hash()
    {
        SkipWithoutDatabase();
        var agent = await Agents.CreateAsync("reuse-found", null, Ct);
        var turn = await TurnWithCompletedStepAsync(agent, "sha256:found");
        await AbandonAsync(turn);

        var found = await Steps.FindAbandonedModelResponseAsync(agent, "sha256:found", Ct);

        Assert.NotNull(found);
        Assert.Contains("kept", found);
    }

    [Fact]
    public async Task A_running_turn_or_another_hash_or_another_agent_gives_nothing_to_reuse()
    {
        SkipWithoutDatabase();
        var agent = await Agents.CreateAsync("reuse-miss", null, Ct);
        var other = await Agents.CreateAsync("reuse-other", null, Ct);

        // Still running: its step is live, not left over from a crash.
        await TurnWithCompletedStepAsync(agent, "sha256:live");
        Assert.Null(await Steps.FindAbandonedModelResponseAsync(agent, "sha256:live", Ct));

        // Abandoned, but for a different request.
        var abandoned = await TurnWithCompletedStepAsync(agent, "sha256:one");
        await AbandonAsync(abandoned);
        Assert.Null(await Steps.FindAbandonedModelResponseAsync(agent, "sha256:two", Ct));

        // Abandoned, but for a different agent.
        Assert.Null(await Steps.FindAbandonedModelResponseAsync(other, "sha256:one", Ct));
    }
}
