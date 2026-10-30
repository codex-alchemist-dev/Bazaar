using Bazaar.Server.Agents.Data;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Agents.Data;

public sealed class LeaseRepositoryTests(AgentDb db) : IClassFixture<AgentDb>
{
    private static readonly TimeSpan _lease = TimeSpan.FromMinutes(5);

    private LeaseRepository Leases => new(db.DataSource);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private async Task<Guid> NewAgentAsync()
    {
        var id = Guid.CreateVersion7();
        await db.ExecAsync("INSERT INTO agent (agent_id, name, state) VALUES (@id, 'lease test', 'active')", ("id", id));
        return id;
    }

    /// <summary>Makes a lease expire in the database clock, without waiting.</summary>
    private Task ExpireAsync(Guid agentId) =>
        db.ExecAsync("UPDATE agent_lease SET expires_at = now() - interval '1 second' WHERE agent_id = @id", ("id", agentId));

    [Fact]
    public async Task A_free_lease_is_taken_once()
    {
        SkipWithoutDatabase();
        var agent = await NewAgentAsync();

        Assert.True(await Leases.TryAcquireAsync(agent, "turn-a", _lease, Ct));
        Assert.False(await Leases.TryAcquireAsync(agent, "turn-b", _lease, Ct));
        Assert.Equal("turn-a", (await Leases.GetAsync(agent, Ct))!.Holder);
    }

    [Fact]
    public async Task Concurrent_acquires_give_exactly_one_winner()
    {
        SkipWithoutDatabase();
        var agent = await NewAgentAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(i => Leases.TryAcquireAsync(agent, $"mention-{i}", _lease, Ct)));

        Assert.Equal(1, results.Count(r => r));
    }

    [Fact]
    public async Task An_expired_lease_can_be_taken_by_a_new_holder()
    {
        SkipWithoutDatabase();
        var agent = await NewAgentAsync();
        Assert.True(await Leases.TryAcquireAsync(agent, "crashed", _lease, Ct));

        await ExpireAsync(agent);

        Assert.True(await Leases.TryAcquireAsync(agent, "recovery", _lease, Ct));
        Assert.Equal("recovery", (await Leases.GetAsync(agent, Ct))!.Holder);
    }

    [Fact]
    public async Task Only_the_holder_can_renew_a_live_lease()
    {
        SkipWithoutDatabase();
        var agent = await NewAgentAsync();
        Assert.True(await Leases.TryAcquireAsync(agent, "turn-a", _lease, Ct));

        Assert.True(await Leases.RenewAsync(agent, "turn-a", _lease, Ct));
        Assert.False(await Leases.RenewAsync(agent, "turn-b", _lease, Ct));
    }

    [Fact]
    public async Task An_expired_lease_cannot_be_renewed()
    {
        SkipWithoutDatabase();
        var agent = await NewAgentAsync();
        Assert.True(await Leases.TryAcquireAsync(agent, "turn-a", _lease, Ct));
        await ExpireAsync(agent);

        Assert.False(await Leases.RenewAsync(agent, "turn-a", _lease, Ct));
    }

    [Fact]
    public async Task Release_frees_the_lease_only_for_its_holder()
    {
        SkipWithoutDatabase();
        var agent = await NewAgentAsync();
        Assert.True(await Leases.TryAcquireAsync(agent, "turn-a", _lease, Ct));

        Assert.False(await Leases.ReleaseAsync(agent, "turn-b", Ct));
        Assert.True(await Leases.ReleaseAsync(agent, "turn-a", Ct));
        Assert.Null(await Leases.GetAsync(agent, Ct));
        Assert.True(await Leases.TryAcquireAsync(agent, "turn-b", _lease, Ct));
    }

    [Fact]
    public async Task A_lease_needs_a_holder()
    {
        SkipWithoutDatabase();
        var agent = await NewAgentAsync();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => Leases.TryAcquireAsync(agent, " ", _lease, Ct));
    }
}
