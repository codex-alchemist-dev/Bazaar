using Bazaar.Server.Kanban;
using Bazaar.Server.Kanban.Data;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Kanban.Data;

/// <summary>The <c>kanban.deadline_scan</c> handler emits <c>task.due_passed</c> once per (task, due_at) on real PostgreSQL (doc 12, WP11).</summary>
public sealed class DeadlineScanTests(KanbanDb db) : IClassFixture<KanbanDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TaskActor _owner = new(TaskActorKind.Owner, Guid.CreateVersion7());

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    // PostgreSQL stores timestamptz at microsecond resolution; round-trip assertions need the same precision.
    private static DateTimeOffset Micros(DateTimeOffset value) => new(value.Ticks / 10 * 10, value.Offset);

    private DeadlineScanHandler NewHandler() => new(
        db.DataSource,
        new TaskDeadlineRepository(db.DataSource),
        new TaskRepository(db.DataSource),
        new TaskEventRepository(db.DataSource),
        new DeadlineScanOptions());

    private async Task<Guid> AddTaskAsync(TaskState state, DateTimeOffset? dueAt)
    {
        var (gcId, _) = await db.CreateGcWithMessageAsync();
        var id = Guid.CreateVersion7();
        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        await new TaskRepository(db.DataSource).InsertAsync(tx,
            new NewTask(id, Guid.CreateVersion7(), "Task", state, null, TaskGate.None, gcId, null, dueAt), Ct);
        await tx.CommitAsync(Ct);
        return id;
    }

    /// <summary>Moves a task's due time with the same optimistic-lock write the task service uses.</summary>
    private async Task SetDueAtAsync(Guid taskId, DateTimeOffset? dueAt)
    {
        var repo = new TaskRepository(db.DataSource);
        var current = (await repo.GetAsync(taskId, Ct))!;
        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        Assert.True(await repo.UpdateAsync(tx, current with { DueAt = dueAt }, current.Version, Ct));
        await tx.CommitAsync(Ct);
    }

    private async Task<List<string>> DuePassedPayloadsAsync(Guid taskId)
    {
        await using var cmd = db.DataSource.CreateCommand(
            "SELECT payload->>'due_at' FROM task_events WHERE task_id = $1 AND kind = $2 ORDER BY seq");
        cmd.Parameters.AddWithValue(taskId);
        cmd.Parameters.AddWithValue(TaskEventKinds.DuePassed);
        var payloads = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            payloads.Add(reader.GetString(0));
        }

        return payloads;
    }

    [Fact]
    public async Task A_past_due_task_gets_exactly_one_due_passed_event_after_two_runs()
    {
        SkipWithoutDatabase();
        var dueAt = Micros(DateTimeOffset.UtcNow.AddHours(-2));
        var task = await AddTaskAsync(TaskState.InProgress, dueAt);
        var handler = NewHandler();

        await handler.ScanAsync(Ct);
        await handler.ScanAsync(Ct);

        var payloads = await DuePassedPayloadsAsync(task);
        var only = Assert.Single(payloads);
        Assert.Equal(dueAt, DateTimeOffset.Parse(only, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_done_task_gets_no_due_passed_event()
    {
        SkipWithoutDatabase();
        var task = await AddTaskAsync(TaskState.Done, DateTimeOffset.UtcNow.AddHours(-2));

        await NewHandler().ScanAsync(Ct);

        Assert.Empty(await DuePassedPayloadsAsync(task));
    }

    [Fact]
    public async Task A_task_without_due_at_gets_no_due_passed_event()
    {
        SkipWithoutDatabase();
        var task = await AddTaskAsync(TaskState.Ready, null);

        await NewHandler().ScanAsync(Ct);

        Assert.Empty(await DuePassedPayloadsAsync(task));
    }

    [Fact]
    public async Task A_task_whose_due_at_changes_gets_a_second_event_for_the_new_due_time()
    {
        SkipWithoutDatabase();
        var first = Micros(DateTimeOffset.UtcNow.AddHours(-3));
        var second = Micros(DateTimeOffset.UtcNow.AddHours(-1));
        var task = await AddTaskAsync(TaskState.Ready, first);
        var handler = NewHandler();

        await handler.ScanAsync(Ct);
        await SetDueAtAsync(task, second);
        await handler.ScanAsync(Ct);
        await handler.ScanAsync(Ct);

        var payloads = (await DuePassedPayloadsAsync(task))
            .Select(p => DateTimeOffset.Parse(p, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        Assert.Equal([first, second], payloads);
    }

    [Fact]
    public async Task A_future_due_date_gets_no_due_passed_event()
    {
        SkipWithoutDatabase();
        var task = await AddTaskAsync(TaskState.Ready, DateTimeOffset.UtcNow.AddDays(2));

        await NewHandler().ScanAsync(Ct);

        Assert.Empty(await DuePassedPayloadsAsync(task));
    }

    [Fact]
    public async Task The_job_kind_is_kanban_deadline_scan()
    {
        SkipWithoutDatabase();
        Assert.Equal("kanban.deadline_scan", NewHandler().Kind);
    }

    [Fact]
    public async Task Hard_mode_still_records_due_passed_events()
    {
        SkipWithoutDatabase();
        var handler = new DeadlineScanHandler(
            db.DataSource,
            new TaskDeadlineRepository(db.DataSource),
            new TaskRepository(db.DataSource),
            new TaskEventRepository(db.DataSource),
            new DeadlineScanOptions { Mode = DeadlineMode.Hard });

        var task = await AddTaskAsync(TaskState.Ready, Micros(DateTimeOffset.UtcNow.AddHours(-2)));

        Assert.Equal(1, await handler.ScanAsync(Ct));
        Assert.Single(await DuePassedPayloadsAsync(task));
    }
}
