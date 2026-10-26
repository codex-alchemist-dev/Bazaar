using Bazaar.Server.Kanban;
using Bazaar.Server.Kanban.Data;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Kanban.Data;

/// <summary>Kanban tables (migration 0012) and their repositories on real PostgreSQL (doc 12, WP1).</summary>
public sealed class KanbanDataTests(KanbanDb db) : IClassFixture<KanbanDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] _allStates = ["backlog", "ready", "in_progress", "review", "done"];

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static NewTask NewTaskFor(Guid gcId, Guid? messageId = null, Guid? projectId = null, TaskState state = TaskState.Backlog) => new(
        Guid.CreateVersion7(), projectId ?? Guid.CreateVersion7(), "Write the thing", state, null, TaskGate.None, gcId, messageId, null);

    private static async Task<string> SqlStateAsync(Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(action);
        return ex.SqlState;
    }

    private async Task<TaskRecord> InsertTaskAsync(NpgsqlTransaction tx, NewTask task) =>
        await new TaskRepository(db.DataSource).InsertAsync(tx, task, Ct);

    private async Task<TaskRecord> InsertCommittedAsync(NewTask task)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        var row = await InsertTaskAsync(tx, task);
        await tx.CommitAsync(Ct);
        return row;
    }

    [Theory]
    [InlineData(TaskState.Backlog)]
    [InlineData(TaskState.Ready)]
    [InlineData(TaskState.InProgress)]
    [InlineData(TaskState.Review)]
    [InlineData(TaskState.Done)]
    public async Task Insert_then_get_round_trips_every_stored_state(TaskState state)
    {
        SkipWithoutDatabase();
        var (gcId, messageId) = await db.CreateGcWithMessageAsync();
        var projectId = Guid.CreateVersion7();
        var owner = Guid.CreateVersion7();
        var due = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
        var task = new NewTask(Guid.CreateVersion7(), projectId, "Ship KB1", state, owner, TaskGate.Council, gcId, messageId, due);

        var inserted = await InsertCommittedAsync(task);
        var read = await new TaskRepository(db.DataSource).GetAsync(task.Id, Ct);

        Assert.NotNull(read);
        Assert.Equal(inserted, read);
        Assert.Equal(task.Id, read.Id);
        Assert.Equal(projectId, read.ProjectId);
        Assert.Equal("Ship KB1", read.Title);
        Assert.Equal(state, read.State);
        Assert.Equal(owner, read.OwnerAgentId);
        Assert.Equal(TaskGate.Council, read.Gate);
        Assert.Equal(gcId, read.OriginGcId);
        Assert.Equal(messageId, read.OriginMessageId);
        Assert.Equal(due, read.DueAt);
        Assert.Null(read.ReviewApprovedBy);
        Assert.Equal(1, read.Version);
    }

    [Fact]
    public async Task Schema_accepts_exactly_the_five_columns_and_rejects_anything_else()
    {
        SkipWithoutDatabase();
        var (gcId, _) = await db.CreateGcWithMessageAsync();
        foreach (var state in _allStates)
        {
            var id = Guid.CreateVersion7();
            await using var ok = db.DataSource.CreateCommand(
                "INSERT INTO tasks (id, project_id, title, state, origin_gc_id) VALUES ($1, $2, 't', $3, $4)");
            ok.Parameters.AddWithValue(id);
            ok.Parameters.AddWithValue(Guid.CreateVersion7());
            ok.Parameters.AddWithValue(state);
            ok.Parameters.AddWithValue(gcId);
            await ok.ExecuteNonQueryAsync(Ct);
        }

        await using var bad = db.DataSource.CreateCommand(
            "INSERT INTO tasks (id, project_id, title, state, origin_gc_id) VALUES ($1, $2, 't', 'blocked', $3)");
        bad.Parameters.AddWithValue(Guid.CreateVersion7());
        bad.Parameters.AddWithValue(Guid.CreateVersion7());
        bad.Parameters.AddWithValue(gcId);
        Assert.Equal("23514", await SqlStateAsync(() => bad.ExecuteNonQueryAsync(Ct)));
    }

    [Fact]
    public async Task Origin_message_must_exist_but_may_be_absent()
    {
        SkipWithoutDatabase();
        var (gcId, messageId) = await db.CreateGcWithMessageAsync();
        var repo = new TaskRepository(db.DataSource);

        await using (var conn = await db.DataSource.OpenConnectionAsync(Ct))
        await using (var tx = await conn.BeginTransactionAsync(Ct))
        {
            var missing = NewTaskFor(gcId, messageId: Guid.CreateVersion7());
            Assert.Equal("23503", await SqlStateAsync(() => repo.InsertAsync(tx, missing, Ct)));
        }

        var pinned = await InsertCommittedAsync(NewTaskFor(gcId, messageId));
        Assert.Equal(messageId, pinned.OriginMessageId);
        var unpinned = await InsertCommittedAsync(NewTaskFor(gcId));
        Assert.Null(unpinned.OriginMessageId);
    }

    [Fact]
    public async Task Update_writes_the_new_state_and_bumps_the_version()
    {
        SkipWithoutDatabase();
        var (gcId, _) = await db.CreateGcWithMessageAsync();
        var repo = new TaskRepository(db.DataSource);
        var created = await InsertCommittedAsync(NewTaskFor(gcId));

        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        var locked = await repo.GetForUpdateAsync(tx, created.Id, Ct);
        Assert.NotNull(locked);
        var approver = Guid.CreateVersion7();
        var approvedAt = DateTimeOffset.UtcNow;
        var ok = await repo.UpdateAsync(tx, locked with { State = TaskState.Review, ReviewApprovedBy = approver, ReviewApprovedAt = approvedAt }, expectedVersion: 1, Ct);
        await tx.CommitAsync(Ct);

        Assert.True(ok);
        var read = await repo.GetAsync(created.Id, Ct);
        Assert.NotNull(read);
        Assert.Equal(TaskState.Review, read.State);
        Assert.Equal(approver, read.ReviewApprovedBy);
        Assert.Equal(2, read.Version);
        Assert.Equal(created.CreatedAt, read.CreatedAt);
    }

    [Fact]
    public async Task Update_with_a_stale_version_changes_nothing()
    {
        SkipWithoutDatabase();
        var (gcId, _) = await db.CreateGcWithMessageAsync();
        var repo = new TaskRepository(db.DataSource);
        var created = await InsertCommittedAsync(NewTaskFor(gcId));

        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        var ok = await repo.UpdateAsync(tx, created with { State = TaskState.Ready }, expectedVersion: 7, Ct);
        await tx.CommitAsync(Ct);

        Assert.False(ok);
        var read = await repo.GetAsync(created.Id, Ct);
        Assert.Equal(TaskState.Backlog, read!.State);
        Assert.Equal(1, read.Version);
    }

    [Fact]
    public async Task Two_writers_holding_the_same_version_let_exactly_one_win()
    {
        SkipWithoutDatabase();
        var (gcId, _) = await db.CreateGcWithMessageAsync();
        var repo = new TaskRepository(db.DataSource);
        var created = await InsertCommittedAsync(NewTaskFor(gcId, state: TaskState.Ready));

        // Both writers read version 1 before either writes, so the loser's UPDATE sees a moved version and matches no row.
        var seenByA = await repo.GetAsync(created.Id, Ct);
        var seenByB = await repo.GetAsync(created.Id, Ct);

        async Task<bool> ClaimAsync(TaskRecord seen, Guid owner)
        {
            await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
            await using var tx = await conn.BeginTransactionAsync(Ct);
            var won = await repo.UpdateAsync(tx, seen with { State = TaskState.InProgress, OwnerAgentId = owner }, seen.Version, Ct);
            if (won)
            {
                await tx.CommitAsync(Ct);
            }
            else
            {
                await tx.RollbackAsync(Ct);
            }

            return won;
        }

        var results = await Task.WhenAll(ClaimAsync(seenByA!, Guid.CreateVersion7()), ClaimAsync(seenByB!, Guid.CreateVersion7()));

        Assert.Equal(1, results.Count(r => r));
        var read = await repo.GetAsync(created.Id, Ct);
        Assert.Equal(TaskState.InProgress, read!.State);
        Assert.Equal(2, read.Version);
        Assert.NotNull(read.OwnerAgentId);
    }

    [Fact]
    public async Task List_by_project_filters_by_state_and_ignores_other_projects()
    {
        SkipWithoutDatabase();
        var (gcId, _) = await db.CreateGcWithMessageAsync();
        var project = Guid.CreateVersion7();
        var repo = new TaskRepository(db.DataSource);
        await InsertCommittedAsync(NewTaskFor(gcId, projectId: project, state: TaskState.Ready));
        await InsertCommittedAsync(NewTaskFor(gcId, projectId: project, state: TaskState.Done));
        await InsertCommittedAsync(NewTaskFor(gcId, projectId: Guid.CreateVersion7(), state: TaskState.Ready));

        var all = await repo.ListByProjectAsync(project, ct: Ct);
        var ready = await repo.ListByProjectAsync(project, TaskState.Ready, Ct);

        Assert.Equal(2, all.Count);
        Assert.Single(ready);
        Assert.Equal(TaskState.Ready, ready[0].State);
    }

    [Fact]
    public async Task Edges_insert_list_and_delete_with_the_schema_rules()
    {
        SkipWithoutDatabase();
        var (gcId, _) = await db.CreateGcWithMessageAsync();
        var project = Guid.CreateVersion7();
        var a = await InsertCommittedAsync(NewTaskFor(gcId, projectId: project));
        var b = await InsertCommittedAsync(NewTaskFor(gcId, projectId: project));
        var c = await InsertCommittedAsync(NewTaskFor(gcId, projectId: project));
        var otherProject = Guid.CreateVersion7();
        var otherBlocker = await InsertCommittedAsync(NewTaskFor(gcId, projectId: otherProject));
        var otherBlocked = await InsertCommittedAsync(NewTaskFor(gcId, projectId: otherProject));
        var edges = new TaskEdgeRepository(db.DataSource);
        var who = Guid.CreateVersion7();

        await using (var conn = await db.DataSource.OpenConnectionAsync(Ct))
        await using (var tx = await conn.BeginTransactionAsync(Ct))
        {
            await edges.InsertAsync(tx, new TaskEdge(a.Id, b.Id), who, Ct);
            await edges.InsertAsync(tx, new TaskEdge(b.Id, c.Id), who, Ct);
            await edges.InsertAsync(tx, new TaskEdge(otherBlocker.Id, otherBlocked.Id), who, Ct);
            await tx.CommitAsync(Ct);
        }

        Assert.Equal(new TaskEdge(a.Id, b.Id), (await edges.ListBlockersAsync(b.Id, ct: Ct)).Single());
        var projectEdges = await edges.ListForProjectAsync(project, ct: Ct);
        Assert.Equal(2, projectEdges.Count);
        Assert.Empty(await edges.ListForProjectAsync(Guid.CreateVersion7(), ct: Ct));

        await using (var conn = await db.DataSource.OpenConnectionAsync(Ct))
        await using (var tx = await conn.BeginTransactionAsync(Ct))
        {
            Assert.True(await edges.DeleteAsync(tx, new TaskEdge(a.Id, b.Id), Ct));
            Assert.False(await edges.DeleteAsync(tx, new TaskEdge(a.Id, b.Id), Ct));
            await tx.CommitAsync(Ct);
        }

        Assert.Single(await edges.ListForProjectAsync(project, ct: Ct));
    }

    [Fact]
    public async Task Edge_constraints_reject_self_duplicate_and_unknown_endpoints()
    {
        SkipWithoutDatabase();
        var (gcId, _) = await db.CreateGcWithMessageAsync();
        var a = await InsertCommittedAsync(NewTaskFor(gcId));
        var b = await InsertCommittedAsync(NewTaskFor(gcId));
        var edges = new TaskEdgeRepository(db.DataSource);
        var who = Guid.CreateVersion7();

        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using (var tx = await conn.BeginTransactionAsync(Ct))
        {
            await edges.InsertAsync(tx, new TaskEdge(a.Id, b.Id), who, Ct);
            await tx.CommitAsync(Ct);
        }

        await using (var tx = await conn.BeginTransactionAsync(Ct))
        {
            Assert.Equal("23514", await SqlStateAsync(() => edges.InsertAsync(tx, new TaskEdge(a.Id, a.Id), who, Ct)));
        }

        await using (var tx = await conn.BeginTransactionAsync(Ct))
        {
            Assert.Equal("23505", await SqlStateAsync(() => edges.InsertAsync(tx, new TaskEdge(a.Id, b.Id), who, Ct)));
        }

        await using (var tx = await conn.BeginTransactionAsync(Ct))
        {
            Assert.Equal("23503", await SqlStateAsync(() => edges.InsertAsync(tx, new TaskEdge(Guid.CreateVersion7(), b.Id), who, Ct)));
        }
    }

    [Fact]
    public async Task Links_are_unique_per_task_kind_and_ref()
    {
        SkipWithoutDatabase();
        var (gcId, _) = await db.CreateGcWithMessageAsync();
        var task = await InsertCommittedAsync(NewTaskFor(gcId));
        var links = new TaskLinkRepository(db.DataSource);
        var pr = new TaskLink(task.Id, TaskLinkKind.Pr, "codex-alchemist-dev/Bazaar#42");

        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using (var tx = await conn.BeginTransactionAsync(Ct))
        {
            Assert.True(await links.InsertAsync(tx, pr, Ct));
            Assert.False(await links.InsertAsync(tx, pr, Ct));
            Assert.True(await links.InsertAsync(tx, new TaskLink(task.Id, TaskLinkKind.VaultDoc, "docs/12.md"), Ct));
            await tx.CommitAsync(Ct);
        }

        var listed = await links.ListAsync(task.Id, ct: Ct);
        Assert.Equal([TaskLinkKind.Pr, TaskLinkKind.VaultDoc], listed.Select(l => l.Kind));

        await using (var tx = await conn.BeginTransactionAsync(Ct))
        {
            Assert.True(await links.DeleteAsync(tx, pr, Ct));
            Assert.False(await links.DeleteAsync(tx, pr, Ct));
            await tx.CommitAsync(Ct);
        }

        Assert.Single(await links.ListAsync(task.Id, ct: Ct));
    }

    [Fact]
    public async Task Link_kind_is_checked_by_the_database()
    {
        SkipWithoutDatabase();
        var (gcId, _) = await db.CreateGcWithMessageAsync();
        var task = await InsertCommittedAsync(NewTaskFor(gcId));

        await using var cmd = db.DataSource.CreateCommand("INSERT INTO task_links (task_id, kind, ref) VALUES ($1, 'issue', '1')");
        cmd.Parameters.AddWithValue(task.Id);
        Assert.Equal("23514", await SqlStateAsync(() => cmd.ExecuteNonQueryAsync(Ct)));
    }

    [Fact]
    public async Task Events_append_in_seq_order_and_can_be_paged()
    {
        SkipWithoutDatabase();
        var (gcId, _) = await db.CreateGcWithMessageAsync();
        var task = await InsertCommittedAsync(NewTaskFor(gcId));
        var events = new TaskEventRepository(db.DataSource);

        var seqs = new List<long>();
        await using (var conn = await db.DataSource.OpenConnectionAsync(Ct))
        await using (var tx = await conn.BeginTransactionAsync(Ct))
        {
            seqs.Add(await events.AppendAsync(tx, new NewTaskEvent(task.Id, TaskEventKinds.Created, "system", "{}"), Ct));
            seqs.Add(await events.AppendAsync(tx, new NewTaskEvent(task.Id, TaskEventKinds.Transitioned, "agent:alice",
                """{"from":"backlog","to":"ready"}"""), Ct));
            await tx.CommitAsync(Ct);
        }

        Assert.True(seqs[1] > seqs[0]);
        var all = await events.ListAsync(task.Id, ct: Ct);
        Assert.Equal(seqs, all.Select(e => e.Seq));
        Assert.Equal(TaskEventKinds.Transitioned, all[1].Kind);
        Assert.Contains("\"to\": \"ready\"", all[1].PayloadJson);

        var page = await events.ListAsync(task.Id, afterSeq: seqs[0], limit: 1, ct: Ct);
        Assert.Single(page);
        Assert.Equal(seqs[1], page[0].Seq);
    }

    [Fact]
    public async Task Events_cannot_be_updated_deleted_or_truncated()
    {
        SkipWithoutDatabase();
        var (gcId, _) = await db.CreateGcWithMessageAsync();
        var task = await InsertCommittedAsync(NewTaskFor(gcId));
        var events = new TaskEventRepository(db.DataSource);
        await using (var conn = await db.DataSource.OpenConnectionAsync(Ct))
        await using (var tx = await conn.BeginTransactionAsync(Ct))
        {
            await events.AppendAsync(tx, new NewTaskEvent(task.Id, TaskEventKinds.Created, "system", "{}"), Ct);
            await tx.CommitAsync(Ct);
        }

        await using var conn2 = await db.DataSource.OpenConnectionAsync(Ct);
        await using var update = new NpgsqlCommand("UPDATE task_events SET actor = 'x'", conn2);
        Assert.Equal("23001", await SqlStateAsync(() => update.ExecuteNonQueryAsync(Ct)));
        await using var delete = new NpgsqlCommand("DELETE FROM task_events", conn2);
        Assert.Equal("23001", await SqlStateAsync(() => delete.ExecuteNonQueryAsync(Ct)));
        await using var truncate = new NpgsqlCommand("TRUNCATE task_events", conn2);
        Assert.Equal("23001", await SqlStateAsync(() => truncate.ExecuteNonQueryAsync(Ct)));
    }

    [Fact]
    public async Task Runtime_role_can_move_state_but_cannot_rewrite_identity_or_delete_tasks()
    {
        SkipWithoutDatabase();
        var (gcId, _) = await db.CreateGcWithMessageAsync();
        var task = await InsertCommittedAsync(NewTaskFor(gcId));

        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using (var setRole = new NpgsqlCommand("SET ROLE bazaar_app", conn))
            await setRole.ExecuteNonQueryAsync(Ct);
        try
        {
            await using (var move = new NpgsqlCommand("UPDATE tasks SET state = 'ready', version = version + 1 WHERE id = $1", conn))
            {
                move.Parameters.AddWithValue(task.Id);
                Assert.Equal(1, await move.ExecuteNonQueryAsync(Ct));
            }

            await using (var retitle = new NpgsqlCommand("UPDATE tasks SET title = 'changed' WHERE id = $1", conn))
            {
                retitle.Parameters.AddWithValue(task.Id);
                Assert.Equal("42501", await SqlStateAsync(() => retitle.ExecuteNonQueryAsync(Ct)));
            }

            await using var delete = new NpgsqlCommand("DELETE FROM tasks", conn);
            Assert.Equal("42501", await SqlStateAsync(() => delete.ExecuteNonQueryAsync(Ct)));
        }
        finally
        {
            await using var reset = new NpgsqlCommand("RESET ROLE", conn);
            await reset.ExecuteNonQueryAsync(Ct);
        }
    }
}
