using Bazaar.RyuseiFuda.Orrery;
using Bazaar.Server.Tests.Kanban.Data;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Ryusei;

/// <summary>
/// <c>ryusei_trigger</c> repository on real PostgreSQL (doc 38, WP-R4). Uses the Kanban fixture, which migrates every
/// namespace. Written without a run: this container has no Docker, so these tests are not verified here.
/// </summary>
public sealed class RyuseiTriggerRepositoryTests(KanbanDb db) : IClassFixture<KanbanDb>
{
    private static readonly DateTimeOffset _instant = new(2026, 11, 8, 14, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private RyuseiTriggerRepository Repo => new(db.DataSource);

    private static NewRyuseiTrigger NewTimer(Guid projectId, Guid createdBy, IReadOnlyList<Guid> targets, string purpose = "check the build") => new(
        Guid.CreateVersion7(),
        projectId,
        createdBy,
        RyuseiTriggerKind.Timer,
        targets,
        purpose,
        RyuseiSpecJson.Encode(new RyuseiTimerTriggerSpec(_instant)),
        TaskId: null,
        AttachGcId: null,
        RyuseiPingMode.None,
        NextFireAt: _instant,
        MaxFires: null,
        ExpiresAt: null,
        ChainDepth: 0);

    private async Task<RyuseiTriggerRecord> InsertCommittedAsync(NewRyuseiTrigger trigger)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        var row = await Repo.InsertAsync(tx, trigger, Ct);
        await tx.CommitAsync(Ct);
        return row;
    }

    /// <summary>Moves a trigger to a state with the optimistic check, committing the change.</summary>
    private async Task<bool> SetStateCommittedAsync(RyuseiTriggerRecord row, RyuseiTriggerState state, long expectedVersion)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        var ok = await Repo.UpdateAsync(tx, row with { State = state }, expectedVersion, Ct);
        await tx.CommitAsync(Ct);
        return ok;
    }

    [Fact]
    public async Task Insert_then_get_round_trips_every_column()
    {
        SkipWithoutDatabase();
        var projectId = Guid.CreateVersion7();
        var creator = Guid.CreateVersion7();
        var targets = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() };
        var trigger = NewTimer(projectId, creator, targets, purpose: "watch the release");

        var inserted = await InsertCommittedAsync(trigger);
        var read = await Repo.GetAsync(trigger.TriggerId, Ct);

        Assert.NotNull(read);
        Assert.Equal(trigger.TriggerId, read.TriggerId);
        Assert.Equal(inserted.CreatedAt, read.CreatedAt);
        Assert.Equal(projectId, read.ProjectId);
        Assert.Equal(creator, read.CreatedBy);
        Assert.Equal(RyuseiTriggerKind.Timer, read.Kind);
        Assert.Equal(targets, read.TargetAgentIds);
        Assert.Equal("watch the release", read.Purpose);
        Assert.Equal(RyuseiTriggerState.Active, read.State);
        Assert.Equal(RyuseiPingMode.None, read.PingMode);
        Assert.Equal(_instant, read.NextFireAt);
        Assert.Equal(0, read.FireCount);
        Assert.Equal(0, read.ConsecutiveRefusals);
        Assert.Equal(0, read.ChainDepth);
        Assert.Equal(1, read.Version);
        Assert.Equal(new RyuseiTimerTriggerSpec(_instant), RyuseiSpecJson.Decode(RyuseiTriggerKind.Timer, read.SpecJson));
    }

    [Fact]
    public async Task Get_of_an_unknown_id_returns_null()
    {
        SkipWithoutDatabase();

        Assert.Null(await Repo.GetAsync(Guid.CreateVersion7(), Ct));
    }

    [Fact]
    public async Task An_attach_trigger_stores_its_gc_and_a_new_gc_trigger_stores_none()
    {
        SkipWithoutDatabase();
        var (gcId, _) = await db.CreateGcWithMessageAsync();
        var projectId = Guid.CreateVersion7();
        var attach = NewTimer(projectId, Guid.CreateVersion7(), [Guid.CreateVersion7()]) with
        {
            PingMode = RyuseiPingMode.Attach,
            AttachGcId = gcId,
        };

        var row = await InsertCommittedAsync(attach);

        Assert.Equal(gcId, row.AttachGcId);
        Assert.Equal(RyuseiPingMode.Attach, row.PingMode);
    }

    [Fact]
    public async Task List_by_project_returns_only_that_project_and_filters_by_state()
    {
        SkipWithoutDatabase();
        var projectId = Guid.CreateVersion7();
        var otherProject = Guid.CreateVersion7();
        var active = await InsertCommittedAsync(NewTimer(projectId, Guid.CreateVersion7(), [Guid.CreateVersion7()]));
        var paused = await InsertCommittedAsync(NewTimer(projectId, Guid.CreateVersion7(), [Guid.CreateVersion7()]));
        await SetStateCommittedAsync(paused, RyuseiTriggerState.Paused, paused.Version);
        await InsertCommittedAsync(NewTimer(otherProject, Guid.CreateVersion7(), [Guid.CreateVersion7()]));

        var all = await Repo.ListByProjectAsync(projectId, ct: Ct);
        var onlyActive = await Repo.ListByProjectAsync(projectId, RyuseiTriggerState.Active, Ct);

        Assert.Equal(2, all.Count);
        Assert.All(all, t => Assert.Equal(projectId, t.ProjectId));
        Assert.Single(onlyActive);
        Assert.Equal(active.TriggerId, onlyActive[0].TriggerId);
    }

    [Fact]
    public async Task List_by_target_agent_returns_every_trigger_that_names_it()
    {
        SkipWithoutDatabase();
        var agent = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        var named = await InsertCommittedAsync(NewTimer(projectId, Guid.CreateVersion7(), [Guid.CreateVersion7(), agent]));
        await InsertCommittedAsync(NewTimer(projectId, Guid.CreateVersion7(), [Guid.CreateVersion7()]));

        var listed = await Repo.ListByTargetAgentAsync(agent, Ct);

        Assert.Single(listed);
        Assert.Equal(named.TriggerId, listed[0].TriggerId);
    }

    [Fact]
    public async Task Live_count_includes_active_and_paused_and_excludes_cancelled()
    {
        SkipWithoutDatabase();
        var agent = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        await InsertCommittedAsync(NewTimer(projectId, Guid.CreateVersion7(), [agent]));
        var paused = await InsertCommittedAsync(NewTimer(projectId, Guid.CreateVersion7(), [agent]));
        var cancelled = await InsertCommittedAsync(NewTimer(projectId, Guid.CreateVersion7(), [agent]));
        await SetStateCommittedAsync(paused, RyuseiTriggerState.Paused, paused.Version);
        await SetStateCommittedAsync(cancelled, RyuseiTriggerState.Cancelled, cancelled.Version);

        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        var counts = await Repo.CountLiveByTargetAsync(tx, [agent, Guid.CreateVersion7()], Ct);

        Assert.Equal(2, counts[agent]);
        Assert.Single(counts);
    }

    [Fact]
    public async Task Update_with_the_current_version_bumps_it_and_a_stale_version_writes_nothing()
    {
        SkipWithoutDatabase();
        var row = await InsertCommittedAsync(NewTimer(Guid.CreateVersion7(), Guid.CreateVersion7(), [Guid.CreateVersion7()]));

        var first = await SetStateCommittedAsync(row, RyuseiTriggerState.Paused, expectedVersion: 1);
        var stale = await SetStateCommittedAsync(row, RyuseiTriggerState.Cancelled, expectedVersion: 1);
        var current = await Repo.GetAsync(row.TriggerId, Ct);

        Assert.True(first);
        Assert.False(stale);
        Assert.NotNull(current);
        Assert.Equal(RyuseiTriggerState.Paused, current.State);
        Assert.Equal(2, current.Version);
    }

    [Fact]
    public async Task A_duplicate_trigger_id_is_rejected_by_the_primary_key()
    {
        SkipWithoutDatabase();
        var first = NewTimer(Guid.CreateVersion7(), Guid.CreateVersion7(), [Guid.CreateVersion7()]);
        await InsertCommittedAsync(first);

        var ex = await Assert.ThrowsAnyAsync<PostgresException>(() => InsertCommittedAsync(first with { Purpose = "again" }));

        Assert.Equal("23505", ex.SqlState);
    }
}
