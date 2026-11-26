using Bazaar.Contracts.Gc;
using Bazaar.Server.PsychWard.Abstractions;

namespace Bazaar.Server.PsychWard.Tests;

public class PgStoreTests(PsychWardDb db) : IClassFixture<PsychWardDb>
{
    private static WardStay NewStay(Guid agent, DateTimeOffset at) =>
        new(Guid.CreateVersion7(), agent, WardTrigger.LoopGuard, "ping_pong: too many bounces", at);

    [Fact]
    public async Task A_stay_round_trips_with_its_check_in_and_discharge()
    {
        var store = new PgWardStayStore(db.DataSource);
        var agent = Guid.CreateVersion7();
        var t = new DateTimeOffset(2026, 11, 22, 18, 0, 0, TimeSpan.Zero);
        var stay = NewStay(agent, t);
        await store.SaveAsync(stay, CancellationToken.None);

        var checkedIn = stay with { CheckIn = new WardCheckIn("a", "b", "c", "d") };
        await store.SaveAsync(checkedIn, CancellationToken.None);
        Assert.Equal(checkedIn, await store.GetLatestAsync(agent, CancellationToken.None));
        Assert.Contains(checkedIn, await store.ListActiveAsync(CancellationToken.None));

        var reviewer = Guid.CreateVersion7();
        var done = checkedIn with { DischargedAt = t.AddHours(2), ReviewerId = reviewer };
        await store.SaveAsync(done, CancellationToken.None);
        Assert.Equal(done, await store.GetLatestAsync(agent, CancellationToken.None));
        Assert.DoesNotContain(done, await store.ListActiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_database_allows_one_active_stay_per_agent_but_a_new_one_after_discharge()
    {
        var store = new PgWardStayStore(db.DataSource);
        var agent = Guid.CreateVersion7();
        var t = new DateTimeOffset(2026, 11, 22, 19, 0, 0, TimeSpan.Zero);
        var first = NewStay(agent, t);
        await store.SaveAsync(first, CancellationToken.None);

        await Assert.ThrowsAsync<WardStayConflictException>(() => store.SaveAsync(NewStay(agent, t.AddMinutes(1)), CancellationToken.None));

        await store.SaveAsync(first with { DischargedAt = t.AddMinutes(2) }, CancellationToken.None);
        var second = NewStay(agent, t.AddMinutes(3));
        await store.SaveAsync(second, CancellationToken.None);
        Assert.Equal(second, await store.GetLatestAsync(agent, CancellationToken.None));
    }

    [Fact]
    public async Task The_service_refuses_a_racing_admission_instead_of_throwing()
    {
        var store = new PgWardStayStore(db.DataSource);
        var agent = Guid.CreateVersion7();
        using var a = new PsychWardService(store, new InMemoryVentStore(), new PsychWardSettings());
        using var b = new PsychWardService(store, new InMemoryVentStore(), new PsychWardSettings());

        var results = await Task.WhenAll(
            a.AdmitAsync(agent, WardTrigger.LoopGuard, null, CancellationToken.None),
            b.AdmitAsync(agent, WardTrigger.LoopGuard, null, CancellationToken.None));

        Assert.Single(results, r => r.Accepted);
        Assert.Single(await store.ListActiveAsync(CancellationToken.None), s => s.AgentId == agent);
    }

    [Fact]
    public async Task Vents_are_keyed_by_failure_and_survive_status_updates()
    {
        var store = new PgVentStore(db.DataSource);
        var agent = Guid.CreateVersion7();
        var vent = new VentDraft("fail-pg-1", agent, "what", "change", "unsure", VentStatus.Held, new DateTimeOffset(2026, 11, 22, 19, 0, 0, TimeSpan.Zero));

        var first = await store.GetOrAddAsync(vent, CancellationToken.None);
        var again = await store.GetOrAddAsync(vent with { WhatHappened = "other" }, CancellationToken.None);
        Assert.Equal("what", again.WhatHappened);
        Assert.Equal(first.FailureRef, again.FailureRef);
        Assert.Contains((await store.ListByStatusAsync(VentStatus.Held, CancellationToken.None)), v => v.FailureRef == "fail-pg-1");

        await store.UpdateAsync(first with { Status = VentStatus.Published, PublishTarget = "internal_general_gc" }, CancellationToken.None);
        var stored = await store.GetAsync("fail-pg-1", CancellationToken.None);
        Assert.Equal(VentStatus.Published, stored!.Status);
        Assert.Equal("internal_general_gc", stored.PublishTarget);
        Assert.Null(await store.GetAsync("nope", CancellationToken.None));
    }

    [Fact]
    public async Task Open_failures_block_until_resolved_and_never_open_for_an_existing_vent()
    {
        var store = new PgVentStore(db.DataSource);
        var agent = Guid.CreateVersion7();

        await store.AddOpenFailureAsync(agent, "open-1", CancellationToken.None);
        await store.AddOpenFailureAsync(agent, "open-1", CancellationToken.None);
        Assert.True(await store.HasOpenFailureAsync(agent, CancellationToken.None));

        await store.ResolveFailureAsync("open-1", CancellationToken.None);
        Assert.False(await store.HasOpenFailureAsync(agent, CancellationToken.None));

        await store.GetOrAddAsync(new VentDraft("done-1", agent, "a", "b", "c", VentStatus.Draft, DateTimeOffset.UtcNow), CancellationToken.None);
        await store.AddOpenFailureAsync(agent, "done-1", CancellationToken.None);
        Assert.False(await store.HasOpenFailureAsync(agent, CancellationToken.None));
    }

    private sealed class FixedOwner(GcPrincipal? owner) : IWardOwnerSource
    {
        public Task<GcPrincipal?> GetOwnerAsync(CancellationToken cancellationToken) => Task.FromResult(owner);
    }

    [Fact]
    public async Task The_directory_reads_agent_names_and_takes_the_owner_from_the_source()
    {
        var agentId = Guid.CreateVersion7();
        await using (var create = db.DataSource.CreateCommand("INSERT INTO agent (agent_id, name) VALUES (@id, 'Charles')"))
        {
            create.Parameters.AddWithValue("id", agentId);
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var owner = new GcPrincipal(Guid.CreateVersion7(), PrincipalKind.Owner, "Boss");
        var directory = new PgWardPrincipalDirectory(db.DataSource, new FixedOwner(owner));

        Assert.Equal(new GcPrincipal(agentId, PrincipalKind.Agent, "Charles"), await directory.GetAgentAsync(agentId, CancellationToken.None));
        Assert.Null(await directory.GetAgentAsync(Guid.CreateVersion7(), CancellationToken.None));
        Assert.Equal(owner, await directory.GetOwnerAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_owner_source_reads_the_one_active_owner_and_ignores_coworkers()
    {
        var ownerId = Guid.CreateVersion7();
        await using (var insert = db.DataSource.CreateCommand(
            "INSERT INTO human (id, display_name, role, status) VALUES (@owner, 'Boss', 'owner', 'active'), (@cow, 'Dana', 'coworker', 'active')"))
        {
            insert.Parameters.AddWithValue("owner", ownerId);
            insert.Parameters.AddWithValue("cow", Guid.CreateVersion7());
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var owner = await new PgWardOwnerSource(db.DataSource).GetOwnerAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new GcPrincipal(ownerId, PrincipalKind.Owner, "Boss"), owner);
    }
}
