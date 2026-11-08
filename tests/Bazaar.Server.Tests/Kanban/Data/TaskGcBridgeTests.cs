using Bazaar.Server.Gc.Write;
using Bazaar.Server.Kanban;
using Bazaar.Server.Kanban.Data;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Kanban.Data;

/// <summary>The Kanban-to-GC bridge: reason lines per committed change and pin-from-message (doc 12, WP6).</summary>
public sealed class TaskGcBridgeTests(KanbanDb db) : IClassFixture<KanbanDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TaskActor _owner = new(TaskActorKind.Owner, Guid.CreateVersion7());

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private sealed class NoopNotifier : IGcNotifier
    {
        public void Notify(Guid gcId, long seq)
        {
        }
    }

    private GcLogWriter NewWriter() => new(db.DataSource, new NoopNotifier());

    private sealed record Board(Guid ProjectId, Guid GcId);

    private async Task<Board> NewBoardAsync()
    {
        var (gcId, _) = await db.CreateGcWithMessageAsync();
        return new Board(Guid.CreateVersion7(), gcId);
    }

    private async Task<Guid> AddTaskAsync(Guid projectId, Guid gcId, TaskState state)
    {
        var id = Guid.CreateVersion7();
        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        await new TaskRepository(db.DataSource).InsertAsync(tx,
            new NewTask(id, projectId, "Task", state, null, TaskGate.None, gcId, null, null), Ct);
        await tx.CommitAsync(Ct);
        return id;
    }

    private async Task<TaskRecord> ReadAsync(Guid id) => (await new TaskRepository(db.DataSource).GetAsync(id, Ct))!;

    /// <summary>The system lines a GC holds, in seq order: client message id and payload text.</summary>
    private async Task<List<(string Key, string Payload)>> SystemLinesAsync(Guid gcId)
    {
        await using var cmd = db.DataSource.CreateCommand("""
            SELECT client_msg_id, payload::text FROM gc_event
            WHERE gc_id = $1 AND kind = 'system' AND author_kind = 'system'
            ORDER BY seq
            """);
        cmd.Parameters.AddWithValue(gcId);
        var result = new List<(string, string)>();
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            result.Add((reader.GetString(0), reader.GetString(1)));
        }

        return result;
    }

    private async Task<long> LatestEventSeqAsync(Guid taskId, string kind)
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT max(seq) FROM task_events WHERE task_id = $1 AND kind = $2");
        cmd.Parameters.AddWithValue(taskId);
        cmd.Parameters.AddWithValue(kind);
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task A_transition_posts_one_reason_line_keyed_by_its_event()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var task = await AddTaskAsync(board.ProjectId, board.GcId, TaskState.Ready);
        var service = new TaskService(db.DataSource, gcLog: NewWriter());

        var result = await service.TransitionAsync(task, TaskState.InProgress, _owner, "starting work", 1, Ct);

        Assert.Equal(TransitionStatus.Applied, result.Status);
        var seq = await LatestEventSeqAsync(task, TaskEventKinds.Transitioned);
        var line = Assert.Single(await SystemLinesAsync(board.GcId));
        Assert.Equal($"kanban:{task:D}:{seq}", line.Key);
        Assert.Contains("\"kind\": \"kanban.reason\"", line.Payload);
        Assert.Contains("\"from\": \"ready\"", line.Payload);
        Assert.Contains("\"to\": \"in_progress\"", line.Payload);
        Assert.Contains("\"reason\": \"starting work\"", line.Payload);
    }

    [Fact]
    public async Task Each_transition_gets_its_own_line()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var task = await AddTaskAsync(board.ProjectId, board.GcId, TaskState.Ready);
        var service = new TaskService(db.DataSource, gcLog: NewWriter());

        await service.TransitionAsync(task, TaskState.InProgress, _owner, "start", 1, Ct);
        await service.TransitionAsync(task, TaskState.Review, _owner, "review", 2, Ct);

        var lines = await SystemLinesAsync(board.GcId);
        Assert.Equal(2, lines.Count);
        Assert.NotEqual(lines[0].Key, lines[1].Key);
    }

    [Fact]
    public async Task Refused_and_invalid_transitions_post_no_line()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var blocker = await AddTaskAsync(board.ProjectId, board.GcId, TaskState.Ready);
        var blocked = await AddTaskAsync(board.ProjectId, board.GcId, TaskState.Ready);
        var backlog = await AddTaskAsync(board.ProjectId, board.GcId, TaskState.Backlog);
        var service = new TaskService(db.DataSource, gcLog: NewWriter());
        await service.AddBlockerAsync(blocked, blocker, _owner, Ct);
        var before = (await SystemLinesAsync(board.GcId)).Count;

        var guard = await service.TransitionAsync(blocked, TaskState.InProgress, _owner, "start", 1, Ct);
        var invalid = await service.TransitionAsync(backlog, TaskState.Done, _owner, null, 1, Ct);
        var stale = await service.TransitionAsync(backlog, TaskState.Ready, _owner, "ready", 99, Ct);

        Assert.Equal(TransitionStatus.Refused, guard.Status);
        Assert.Equal(TransitionStatus.Invalid, invalid.Status);
        Assert.Equal(TransitionStatus.Conflict, stale.Status);
        Assert.Equal(before, (await SystemLinesAsync(board.GcId)).Count);
    }

    [Fact]
    public async Task Completing_the_last_blocker_posts_an_auto_ready_line_into_the_dependents_gc()
    {
        SkipWithoutDatabase();
        var blockerBoard = await NewBoardAsync();
        var dependentGc = await db.CreateGcWithMessageAsync();
        var projectId = Guid.CreateVersion7();
        var blocker = await AddTaskAsync(projectId, blockerBoard.GcId, TaskState.Review);
        var dependent = await AddTaskAsync(projectId, dependentGc.GcId, TaskState.Backlog);
        var service = new TaskService(db.DataSource, gcLog: NewWriter());
        await service.AddBlockerAsync(dependent, blocker, _owner, Ct);

        await service.TransitionAsync(blocker, TaskState.Done, _owner, "shipped", 1, Ct);

        Assert.Equal(TaskState.Ready, (await ReadAsync(dependent)).State);
        var readySeq = await LatestEventSeqAsync(dependent, TaskEventKinds.Transitioned);
        var line = Assert.Single(await SystemLinesAsync(dependentGc.GcId));
        Assert.Equal($"kanban:{dependent:D}:{readySeq}", line.Key);
        Assert.Contains("auto-ready: blockers done", line.Payload);
        Assert.Contains("\"to\": \"ready\"", line.Payload);
    }

    [Fact]
    public async Task A_service_without_a_writer_applies_changes_and_posts_nothing()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var task = await AddTaskAsync(board.ProjectId, board.GcId, TaskState.Ready);
        var service = new TaskService(db.DataSource);

        var result = await service.TransitionAsync(task, TaskState.InProgress, _owner, "start", 1, Ct);

        Assert.Equal(TransitionStatus.Applied, result.Status);
        Assert.Empty(await SystemLinesAsync(board.GcId));
    }

    [Fact]
    public async Task A_transition_in_an_archived_gc_stands_without_its_line()
    {
        SkipWithoutDatabase();
        var board = await NewBoardAsync();
        var task = await AddTaskAsync(board.ProjectId, board.GcId, TaskState.Ready);
        await using (var archive = db.DataSource.CreateCommand("UPDATE gc SET archived_at = now() WHERE gc_id = $1"))
        {
            archive.Parameters.AddWithValue(board.GcId);
            await archive.ExecuteNonQueryAsync(Ct);
        }

        var service = new TaskService(db.DataSource, gcLog: NewWriter());
        var result = await service.TransitionAsync(task, TaskState.InProgress, _owner, "start", 1, Ct);

        Assert.Equal(TransitionStatus.Applied, result.Status);
        Assert.Equal(TaskState.InProgress, (await ReadAsync(task)).State);
        Assert.Empty(await SystemLinesAsync(board.GcId));
    }

    [Fact]
    public async Task Pinning_a_message_creates_a_backlog_card_from_its_gc_with_a_gc_link_and_a_line()
    {
        SkipWithoutDatabase();
        var (gcId, messageId) = await db.CreateGcWithMessageAsync();
        var projectId = Guid.CreateVersion7();
        var service = new TaskService(db.DataSource, gcLog: NewWriter());

        var result = await service.CreateFromMessageAsync(projectId, gcId, messageId, "Follow up on this", _owner, Ct);

        Assert.Equal(CreateFromMessageStatus.Created, result.Status);
        var taskId = result.TaskId!.Value;
        var row = await ReadAsync(taskId);
        Assert.Equal(TaskState.Backlog, row.State);
        Assert.Equal(gcId, row.OriginGcId);
        Assert.Equal(messageId, row.OriginMessageId);
        var link = Assert.Single(await new TaskLinkRepository(db.DataSource).ListAsync(taskId, null, Ct));
        Assert.Equal(TaskLinkKind.Gc, link.Kind);
        Assert.Equal(messageId.ToString("D"), link.Ref);
        var seq = await LatestEventSeqAsync(taskId, TaskEventKinds.Created);
        var line = Assert.Single(await SystemLinesAsync(gcId));
        Assert.Equal($"kanban:{taskId:D}:{seq}", line.Key);
        Assert.Contains("pinned from message", line.Payload);
    }

    [Fact]
    public async Task Pinning_a_message_from_another_gc_is_refused_and_creates_nothing()
    {
        SkipWithoutDatabase();
        var (messageGc, messageId) = await db.CreateGcWithMessageAsync();
        var (otherGc, _) = await db.CreateGcWithMessageAsync();
        var projectId = Guid.CreateVersion7();
        var service = new TaskService(db.DataSource, gcLog: NewWriter());

        var result = await service.CreateFromMessageAsync(projectId, otherGc, messageId, "Wrong GC", _owner, Ct);

        Assert.Equal(CreateFromMessageStatus.OtherGc, result.Status);
        Assert.Null(result.TaskId);
        Assert.Empty(await new TaskRepository(db.DataSource).ListByProjectAsync(projectId, null, null, Ct));
        Assert.Empty(await SystemLinesAsync(messageGc));
        Assert.Empty(await SystemLinesAsync(otherGc));
    }

    [Fact]
    public async Task Pinning_an_unknown_message_is_refused_and_creates_nothing()
    {
        SkipWithoutDatabase();
        var (gcId, _) = await db.CreateGcWithMessageAsync();
        var projectId = Guid.CreateVersion7();
        var service = new TaskService(db.DataSource, gcLog: NewWriter());

        var result = await service.CreateFromMessageAsync(projectId, gcId, Guid.CreateVersion7(), "Ghost", _owner, Ct);

        Assert.Equal(CreateFromMessageStatus.UnknownMessage, result.Status);
        Assert.Null(result.TaskId);
        Assert.Empty(await new TaskRepository(db.DataSource).ListByProjectAsync(projectId, null, null, Ct));
    }
}
