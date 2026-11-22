namespace Bazaar.Server.Portability.Postgres.Tests;

/// <summary>The Kanban export reader against the real <c>tasks</c>, <c>task_edges</c>, <c>task_links</c> and <c>task_events</c> tables.</summary>
public sealed class PostgresKanbanExportSourceTests
{
    private static readonly Guid _projectA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid _projectB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");
    private static readonly Guid _t1 = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid _t2 = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid _t3 = Guid.Parse("30000000-0000-0000-0000-000000000003");
    private static readonly Guid _tOther = Guid.Parse("05000000-0000-0000-0000-000000000005");

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }

    private static async Task SeedTaskAsync(ExportReaderDb db, Guid id, Guid project, Guid gc, string state, string gate = "none")
    {
        await db.ExecAsync(
            "INSERT INTO tasks (id, project_id, title, state, gate, origin_gc_id, created_at, updated_at) VALUES ($1, $2, $3, $4, $5, $6, $7, $7)",
            id, project, "task " + id.ToString("N")[..4], state, gate, gc, new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));
    }

    private static async Task<(ExportReaderDb Db, Guid Gc)> SeedAsync(CancellationToken ct)
    {
        var db = await ExportReaderDb.OpenAsync(ct);
        var gc = await db.CreateGcAsync();
        // t1 (done) blocks t2 and t3; t2 (in_progress) blocks t3. t3 is therefore blocked by t2 only. tOther is a board of its own.
        await SeedTaskAsync(db, _t3, _projectA, gc, "backlog", "owner");
        await SeedTaskAsync(db, _t1, _projectA, gc, "done");
        await SeedTaskAsync(db, _t2, _projectA, gc, "in_progress", "council");
        await SeedTaskAsync(db, _tOther, _projectB, gc, "ready");
        await db.ExecAsync("INSERT INTO task_edges (blocker_id, blocked_id, created_by) VALUES ($1, $2, $3)", _t2, _t3, _t1);
        await db.ExecAsync("INSERT INTO task_edges (blocker_id, blocked_id, created_by) VALUES ($1, $2, $3)", _t1, _t3, _t1);
        await db.ExecAsync("INSERT INTO task_edges (blocker_id, blocked_id, created_by) VALUES ($1, $2, $3)", _t1, _t2, _t1);
        await db.ExecAsync("INSERT INTO task_links (task_id, kind, ref) VALUES ($1, 'pr', 'gh:42'), ($1, 'gc', 'abc'), ($1, 'commit', 'deadbeef')", _t2);
        foreach (var (task, from, to) in new (Guid, string, string)[] { (_t2, "ready", "in_progress"), (_t1, "review", "done"), (_tOther, "backlog", "ready") })
        {
            await db.ExecAsync(
                "INSERT INTO task_events (task_id, kind, actor, payload) VALUES ($1, 'task.transitioned', 'owner:x', jsonb_build_object('from', $2::text, 'to', $3::text, 'reason', 'because'))",
                task, from, to);
        }

        await db.ExecAsync("INSERT INTO task_events (task_id, kind, actor, payload) VALUES ($1, 'task.created', 'system', '{\"to\":\"backlog\"}')", _t3);
        return (db, gc);
    }

    [Fact]
    public async Task Tasks_stream_in_id_order_with_derived_blockers_and_sorted_links()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, gc) = await SeedAsync(ct);
        await using var _ = db;
        var source = new PostgresKanbanExportSource(db.DataSource);

        var tasks = await ToListAsync(source.ReadTasksAsync(KanbanExportScope.All, ct));

        Assert.Equal([_tOther, _t1, _t2, _t3], tasks.Select(t => t.Id));
        var t3 = tasks[3];
        Assert.Equal(KanbanColumn.Backlog, t3.Column);
        Assert.Equal(KanbanGate.Owner, t3.Gate);
        Assert.Equal(gc, t3.OriginGcId);
        Assert.Equal([_t2], t3.UnfinishedBlockers);
        Assert.Empty(tasks[1].UnfinishedBlockers);
        Assert.Empty(tasks[2].UnfinishedBlockers);
        // Links sort by stored kind text, then ref; a ref may hold a colon.
        Assert.Equal(
            [(KanbanLinkKind.Commit, "deadbeef"), (KanbanLinkKind.Gc, "abc"), (KanbanLinkKind.Pr, "gh:42")],
            tasks[2].Links.Select(l => (l.Kind, l.Ref)));
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero), t3.CreatedAt);
        Assert.Equal(1L, t3.Version);
        Assert.Null(t3.DueAt);
    }

    [Fact]
    public async Task Edges_order_by_blocker_then_blocked()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, _) = await SeedAsync(ct);
        await using var _ = db;
        var source = new PostgresKanbanExportSource(db.DataSource);

        var edges = await ToListAsync(source.ReadEdgesAsync(KanbanExportScope.All, ct));

        Assert.Equal([(_t1, _t2), (_t1, _t3), (_t2, _t3)], edges.Select(e => (e.BlockerId, e.BlockedId)));
        Assert.Equal(_t1.ToString("D"), edges[0].CreatedBy);
    }

    [Fact]
    public async Task Transitions_are_only_transitioned_events_in_seq_order_with_the_task_gate()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, _) = await SeedAsync(ct);
        await using var _ = db;
        var source = new PostgresKanbanExportSource(db.DataSource);

        var moves = await ToListAsync(source.ReadTransitionsAsync(KanbanExportScope.All, ct));

        Assert.Equal([_t2, _t1, _tOther], moves.Select(m => m.TaskId));
        Assert.True(moves.Zip(moves.Skip(1), (a, b) => a.Seq < b.Seq).All(x => x));
        Assert.Equal(KanbanColumn.Ready, moves[0].From);
        Assert.Equal(KanbanColumn.InProgress, moves[0].To);
        Assert.Equal(KanbanGate.Council, moves[0].Gate);
        Assert.Equal("owner:x", moves[0].Actor);
        Assert.Equal("because", moves[0].Reason);
    }

    [Fact]
    public async Task Scope_limits_every_read_to_the_named_boards()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, _) = await SeedAsync(ct);
        await using var _ = db;
        var source = new PostgresKanbanExportSource(db.DataSource);
        var scope = new KanbanExportScope([_projectB]);

        Assert.Equal([_tOther], (await ToListAsync(source.ReadTasksAsync(scope, ct))).Select(t => t.Id));
        Assert.Empty(await ToListAsync(source.ReadEdgesAsync(scope, ct)));
        Assert.Equal([_tOther], (await ToListAsync(source.ReadTransitionsAsync(scope, ct))).Select(m => m.TaskId));
    }

    [Fact]
    public async Task An_empty_database_yields_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await ExportReaderDb.OpenAsync(ct);
        var source = new PostgresKanbanExportSource(db.DataSource);

        Assert.Empty(await ToListAsync(source.ReadTasksAsync(KanbanExportScope.All, ct)));
        Assert.Empty(await ToListAsync(source.ReadEdgesAsync(KanbanExportScope.All, ct)));
        Assert.Empty(await ToListAsync(source.ReadTransitionsAsync(KanbanExportScope.All, ct)));
    }

    [Fact]
    public async Task A_null_scope_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = new PostgresKanbanExportSource(null!);

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await ToListAsync(source.ReadTasksAsync(null!, ct)));
    }
}
