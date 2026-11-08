using Bazaar.Server.Interrupts;
using Bazaar.Testing;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Interrupts;

public sealed class InterruptStackTests(InterruptDb db) : IClassFixture<InterruptDb>
{
    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Guid NewGuid() => Guid.CreateVersion7();

    /// <summary>Each test gets its own clock and stack over the shared class database; agents are fresh per test.</summary>
    private static (InterruptStackService Stack, FakeClock Clock) NewStack(NpgsqlDataSource dataSource)
    {
        var clock = new FakeClock();
        return (new InterruptStackService(dataSource, clock), clock);
    }

    [Fact]
    public async Task A_first_push_makes_an_active_root_frame()
    {
        SkipWithoutDatabase();
        var (stack, clock) = NewStack(db.DataSource);
        var agent = NewGuid();
        var gc = NewGuid();

        var frame = await stack.PushAsync(agent, InterruptSource.Owner, "Stop and review", gc, ct: Ct);

        Assert.Equal(InterruptFrameState.Active, frame.State);
        Assert.Null(frame.ParentFrameId);
        Assert.Equal(clock.GetUtcNow(), frame.PushedAt);
        Assert.Equal(1, frame.Version);
        var live = await stack.GetStackAsync(agent, Ct);
        Assert.Equal(frame.Id, Assert.Single(live).Id);
    }

    [Fact]
    public async Task A_second_push_suspends_the_top_and_links_it_as_parent()
    {
        SkipWithoutDatabase();
        var (stack, _) = NewStack(db.DataSource);
        var agent = NewGuid();
        var gc = NewGuid();
        var first = await stack.PushAsync(agent, InterruptSource.Owner, "First", gc, ct: Ct);

        var second = await stack.PushAsync(agent, InterruptSource.SystemBuild, "Build broke", gc, ct: Ct);

        Assert.Equal(first.Id, second.ParentFrameId);
        var live = await stack.GetStackAsync(agent, Ct);
        Assert.Equal([second.Id, first.Id], live.Select(f => f.Id));
        Assert.Equal(InterruptFrameState.Active, live[0].State);
        Assert.Equal(InterruptFrameState.Suspended, live[1].State);
        Assert.Equal(2, live[1].Version);
    }

    [Fact]
    public async Task A_third_push_is_refused_and_the_stack_is_unchanged()
    {
        SkipWithoutDatabase();
        var (stack, _) = NewStack(db.DataSource);
        var agent = NewGuid();
        var gc = NewGuid();
        await stack.PushAsync(agent, InterruptSource.Owner, "First", gc, ct: Ct);
        var second = await stack.PushAsync(agent, InterruptSource.Owner, "Second", gc, ct: Ct);
        var before = await stack.GetStackAsync(agent, Ct);

        var error = await Assert.ThrowsAsync<InterruptDepthExceededException>(
            () => stack.PushAsync(agent, InterruptSource.Owner, "Third", gc, ct: Ct));

        Assert.Equal(agent, error.AgentId);
        var after = await stack.GetStackAsync(agent, Ct);
        Assert.Equal(before.Select(f => (f.Id, f.State, f.Version)), after.Select(f => (f.Id, f.State, f.Version)));
        Assert.Equal(second.Id, after[0].Id);
    }

    [Fact]
    public async Task Pop_ends_the_top_and_resumes_its_parent()
    {
        SkipWithoutDatabase();
        var (stack, _) = NewStack(db.DataSource);
        var agent = NewGuid();
        var gc = NewGuid();
        var first = await stack.PushAsync(agent, InterruptSource.Owner, "First", gc, ct: Ct);
        var second = await stack.PushAsync(agent, InterruptSource.Owner, "Second", gc, ct: Ct);

        var resumed = await stack.PopAsync(agent, second.Id, Ct);

        Assert.NotNull(resumed);
        Assert.Equal(first.Id, resumed.Id);
        Assert.Equal(InterruptFrameState.Active, resumed.State);
        var live = await stack.GetStackAsync(agent, Ct);
        Assert.Equal(first.Id, Assert.Single(live).Id);
    }

    [Fact]
    public async Task Popping_the_last_frame_leaves_an_empty_stack()
    {
        SkipWithoutDatabase();
        var (stack, _) = NewStack(db.DataSource);
        var agent = NewGuid();
        var frame = await stack.PushAsync(agent, InterruptSource.Owner, "Only", NewGuid(), ct: Ct);

        var resumed = await stack.PopAsync(agent, frame.Id, Ct);

        Assert.Null(resumed);
        Assert.Empty(await stack.GetStackAsync(agent, Ct));
    }

    [Fact]
    public async Task Pop_is_idempotent_by_frame_id()
    {
        SkipWithoutDatabase();
        var (stack, _) = NewStack(db.DataSource);
        var agent = NewGuid();
        var gc = NewGuid();
        var first = await stack.PushAsync(agent, InterruptSource.Owner, "First", gc, ct: Ct);
        var second = await stack.PushAsync(agent, InterruptSource.Owner, "Second", gc, ct: Ct);
        await stack.PopAsync(agent, second.Id, Ct);
        var afterFirstPop = await stack.GetStackAsync(agent, Ct);

        var repeat = await stack.PopAsync(agent, second.Id, Ct);

        Assert.Null(repeat);
        Assert.Equal(afterFirstPop.Select(f => (f.Id, f.Version)), (await stack.GetStackAsync(agent, Ct)).Select(f => (f.Id, f.Version)));
        Assert.Equal(first.Id, Assert.Single(await stack.GetStackAsync(agent, Ct)).Id);
    }

    [Fact]
    public async Task Popping_a_suspended_frame_is_refused()
    {
        SkipWithoutDatabase();
        var (stack, _) = NewStack(db.DataSource);
        var agent = NewGuid();
        var gc = NewGuid();
        var first = await stack.PushAsync(agent, InterruptSource.Owner, "First", gc, ct: Ct);
        await stack.PushAsync(agent, InterruptSource.Owner, "Second", gc, ct: Ct);

        await Assert.ThrowsAsync<InterruptFrameNotTopException>(() => stack.PopAsync(agent, first.Id, Ct));

        Assert.Equal(2, (await stack.GetStackAsync(agent, Ct)).Count);
    }

    [Fact]
    public async Task Popping_a_frame_of_another_agent_is_refused()
    {
        SkipWithoutDatabase();
        var (stack, _) = NewStack(db.DataSource);
        var owner = NewGuid();
        var other = NewGuid();
        var frame = await stack.PushAsync(owner, InterruptSource.Owner, "Mine", NewGuid(), ct: Ct);

        await Assert.ThrowsAsync<InterruptStackException>(() => stack.PopAsync(other, frame.Id, Ct));

        Assert.Equal(frame.Id, Assert.Single(await stack.GetStackAsync(owner, Ct)).Id);
    }

    [Fact]
    public async Task Identical_system_pushes_within_60_seconds_collapse_into_one_frame()
    {
        SkipWithoutDatabase();
        var (stack, clock) = NewStack(db.DataSource);
        var agent = NewGuid();
        var gc = NewGuid();
        var first = await stack.PushAsync(agent, InterruptSource.SystemBuild, "CI red", gc, ct: Ct);
        clock.Advance(TimeSpan.FromSeconds(59));

        var repeat = await stack.PushAsync(agent, InterruptSource.SystemBuild, "CI red", gc, ct: Ct);

        Assert.Equal(first.Id, repeat.Id);
        Assert.Single(await stack.GetStackAsync(agent, Ct));
    }

    [Fact]
    public async Task An_identical_system_push_after_the_window_is_a_new_frame()
    {
        SkipWithoutDatabase();
        var (stack, clock) = NewStack(db.DataSource);
        var agent = NewGuid();
        var gc = NewGuid();
        await stack.PushAsync(agent, InterruptSource.SystemBuild, "CI red", gc, ct: Ct);
        clock.Advance(InterruptStackService.SystemDedupeWindow + TimeSpan.FromSeconds(1));

        var later = await stack.PushAsync(agent, InterruptSource.SystemBuild, "CI red", gc, ct: Ct);

        Assert.Equal(2, (await stack.GetStackAsync(agent, Ct)).Count);
        Assert.NotNull(later.ParentFrameId);
    }

    [Fact]
    public async Task Owner_pushes_are_never_collapsed()
    {
        SkipWithoutDatabase();
        var (stack, _) = NewStack(db.DataSource);
        var agent = NewGuid();
        var gc = NewGuid();
        await stack.PushAsync(agent, InterruptSource.Owner, "Same words", gc, ct: Ct);

        var second = await stack.PushAsync(agent, InterruptSource.Owner, "Same words", gc, ct: Ct);

        Assert.Equal(2, (await stack.GetStackAsync(agent, Ct)).Count);
        Assert.NotNull(second.ParentFrameId);
    }

    [Fact]
    public async Task The_stack_is_rebuilt_from_the_table_after_a_restart()
    {
        SkipWithoutDatabase();
        var agent = NewGuid();
        var gc = NewGuid();
        var (before, _) = NewStack(db.DataSource);
        var first = await before.PushAsync(agent, InterruptSource.Owner, "First", gc, ct: Ct);
        var second = await before.PushAsync(agent, InterruptSource.SystemSecurity, "Alert", gc, ct: Ct);

        // A new service instance over the same database stands in for a restarted host.
        var (after, _) = NewStack(db.DataSource);
        var live = await after.GetStackAsync(agent, Ct);

        Assert.Equal([second.Id, first.Id], live.Select(f => f.Id));
        var resumed = await after.PopAsync(agent, second.Id, Ct);
        Assert.Equal(first.Id, resumed!.Id);
    }

    [Fact]
    public async Task Concurrent_pushes_for_one_agent_never_exceed_depth_two()
    {
        SkipWithoutDatabase();
        var (stack, _) = NewStack(db.DataSource);
        var agent = NewGuid();
        var gc = NewGuid();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
        {
            try
            {
                await stack.PushAsync(agent, InterruptSource.Owner, $"Push {i}", gc, ct: Ct);
                return true;
            }
            catch (InterruptDepthExceededException)
            {
                return false;
            }
        }));

        Assert.Equal(2, outcomes.Count(ok => ok));
        Assert.Equal(2, (await stack.GetStackAsync(agent, Ct)).Count);
    }

    [Fact]
    public async Task Stacks_of_different_agents_are_independent()
    {
        SkipWithoutDatabase();
        var (stack, _) = NewStack(db.DataSource);
        var gc = NewGuid();
        var a = NewGuid();
        var b = NewGuid();
        await stack.PushAsync(a, InterruptSource.Owner, "A1", gc, ct: Ct);
        await stack.PushAsync(a, InterruptSource.Owner, "A2", gc, ct: Ct);

        var bFrame = await stack.PushAsync(b, InterruptSource.Owner, "B1", gc, ct: Ct);

        Assert.Null(bFrame.ParentFrameId);
        Assert.Equal(2, (await stack.GetStackAsync(a, Ct)).Count);
    }

    [Fact]
    public async Task The_database_refuses_a_second_active_frame_for_one_agent()
    {
        SkipWithoutDatabase();
        var agent = NewGuid();
        var gc = NewGuid();
        await using (var first = db.DataSource.CreateCommand(_insertActive))
        {
            first.Parameters.AddWithValue("id", NewGuid());
            first.Parameters.AddWithValue("agent_id", agent);
            first.Parameters.AddWithValue("gc_id", gc);
            await first.ExecuteNonQueryAsync(Ct);
        }

        await using var second = db.DataSource.CreateCommand(_insertActive);
        second.Parameters.AddWithValue("id", NewGuid());
        second.Parameters.AddWithValue("agent_id", agent);
        second.Parameters.AddWithValue("gc_id", gc);
        await Assert.ThrowsAsync<PostgresException>(() => second.ExecuteNonQueryAsync(Ct));
    }

    [Fact]
    public async Task An_empty_reason_is_refused_before_it_reaches_the_database()
    {
        SkipWithoutDatabase();
        var (stack, _) = NewStack(db.DataSource);

        await Assert.ThrowsAsync<ArgumentException>(() => stack.PushAsync(NewGuid(), InterruptSource.Owner, " ", NewGuid(), ct: Ct));
    }

    private const string _insertActive = """
        INSERT INTO interrupt_frame (id, agent_id, source, reason, gc_id, state, pushed_at, version)
        VALUES (@id, @agent_id, 'owner', 'direct', @gc_id, 'active', now(), 1)
        """;
}
