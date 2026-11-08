using Bazaar.Server.Agents;
using Bazaar.Server.Agents.Data;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Agents.Data;

public sealed class AgentRepositoryTests(AgentDb db) : IClassFixture<AgentDb>
{
    private AgentRepository Repo => new(db.DataSource);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    [Fact]
    public async Task Create_starts_in_created_and_round_trips()
    {
        SkipWithoutDatabase();
        var soul = Guid.CreateVersion7();

        var id = await Repo.CreateAsync("Charles", soul, Ct);
        var agent = await Repo.GetAsync(id, Ct);

        Assert.NotNull(agent);
        Assert.Equal("Charles", agent.Name);
        Assert.Equal(soul, agent.SoulVersionId);
        Assert.Equal(AgentState.Created, agent.State);
    }

    [Fact]
    public async Task Get_returns_null_for_an_unknown_agent()
    {
        SkipWithoutDatabase();
        Assert.Null(await Repo.GetAsync(Guid.CreateVersion7(), Ct));
    }

    [Fact]
    public async Task Create_rejects_an_empty_name()
    {
        SkipWithoutDatabase();
        await Assert.ThrowsAnyAsync<Exception>(() => Repo.CreateAsync("", null, Ct));
    }

    [Fact]
    public async Task SetState_moves_along_the_lifecycle_and_fails_on_a_stale_from_state()
    {
        SkipWithoutDatabase();
        var id = await Repo.CreateAsync("Mara", null, Ct);

        Assert.True(await Repo.SetStateAsync(id, AgentState.Created, AgentState.Onboarding, Ct));
        Assert.True(await Repo.SetStateAsync(id, AgentState.Onboarding, AgentState.Active, Ct));
        Assert.True(await Repo.SetStateAsync(id, AgentState.Active, AgentState.Resting, Ct));
        Assert.False(await Repo.SetStateAsync(id, AgentState.Active, AgentState.Resting, Ct));
        Assert.Equal(AgentState.Resting, (await Repo.GetAsync(id, Ct))!.State);
    }

    [Fact]
    public async Task SetState_refuses_a_move_outside_the_lifecycle_before_touching_the_database()
    {
        SkipWithoutDatabase();
        var id = await Repo.CreateAsync("Iris", null, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Repo.SetStateAsync(id, AgentState.Created, AgentState.Active, Ct));
        Assert.Equal(AgentState.Created, (await Repo.GetAsync(id, Ct))!.State);
    }

    [Fact]
    public async Task Retired_is_terminal()
    {
        SkipWithoutDatabase();
        var id = await Repo.CreateAsync("Oren", null, Ct);
        Assert.True(await Repo.SetStateAsync(id, AgentState.Created, AgentState.Retired, Ct));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Repo.SetStateAsync(id, AgentState.Retired, AgentState.Active, Ct));
    }
}
