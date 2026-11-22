using Bazaar.RyuseiFuda.Kanban;
using Bazaar.RyuseiFuda.Kanban.Data;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Agents.Inbox;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Kanban.Data;

/// <summary>Assigning a card writes one <c>assignment</c> inbox item for the new assignee agent (doc 12, "Assignment wake").</summary>
public sealed class TaskAssignmentInboxTests(KanbanDb db) : IClassFixture<KanbanDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static readonly TaskActor _owner = new(TaskActorKind.Owner, Guid.CreateVersion7());

    private async Task<Guid> AddAgentAsync(string state = "active")
    {
        var id = Guid.CreateVersion7();
        await using var cmd = db.DataSource.CreateCommand("INSERT INTO agent (agent_id, name, state) VALUES ($1, 'assignee', $2)");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(state);
        await cmd.ExecuteNonQueryAsync(Ct);
        return id;
    }

    private async Task<Guid> AddTaskAsync(TaskState state = TaskState.Backlog)
    {
        var (gcId, _) = await db.CreateGcWithMessageAsync();
        var id = Guid.CreateVersion7();
        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        await new TaskRepository(db.DataSource).InsertAsync(tx,
            new NewTask(id, Guid.CreateVersion7(), "Task", state, null, TaskGate.None, gcId, null, null), Ct);
        await tx.CommitAsync(Ct);
        return id;
    }

    private async Task<long> VersionAsync(Guid taskId) => (await new TaskRepository(db.DataSource).GetAsync(taskId, Ct))!.Version;

    private async Task<List<(string Kind, string? SourceRef, string State)>> InboxAsync(Guid agentId)
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT kind, source_ref, state FROM inbox_item WHERE agent_id = $1 ORDER BY created_at");
        cmd.Parameters.AddWithValue(agentId);
        var rows = new List<(string, string?, string)>();
        await using var r = await cmd.ExecuteReaderAsync(Ct);
        while (await r.ReadAsync(Ct))
            rows.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2)));
        return rows;
    }

    [Fact]
    public async Task Assigning_a_card_wakes_the_assignee_once()
    {
        SkipWithoutDatabase();
        var service = new TaskService(db.DataSource);
        var agent = await AddAgentAsync();
        var task = await AddTaskAsync();

        var result = await service.AssignAsync(task, agent, _owner, 1, Ct);

        Assert.Equal(TransitionStatus.Applied, result.Status);
        var rows = await InboxAsync(agent);
        var row = Assert.Single(rows);
        Assert.Equal(InboxKind.Assignment, row.Kind);
        Assert.Equal($"kanban:assign:{task:D}:2", row.SourceRef);
        Assert.Equal(agent, (await new TaskRepository(db.DataSource).GetAsync(task, Ct))!.OwnerAgentId);
    }

    [Fact]
    public async Task Reassigning_wakes_the_new_assignee_and_not_the_old_one()
    {
        SkipWithoutDatabase();
        var service = new TaskService(db.DataSource);
        var first = await AddAgentAsync();
        var second = await AddAgentAsync();
        var task = await AddTaskAsync();

        await service.AssignAsync(task, first, _owner, 1, Ct);
        await service.AssignAsync(task, second, _owner, 2, Ct);

        Assert.Single(await InboxAsync(first));
        Assert.Equal($"kanban:assign:{task:D}:3", Assert.Single(await InboxAsync(second)).SourceRef);
    }

    [Fact]
    public async Task Replaying_an_assignment_writes_nothing_more()
    {
        SkipWithoutDatabase();
        var service = new TaskService(db.DataSource);
        var agent = await AddAgentAsync();
        var task = await AddTaskAsync();

        await service.AssignAsync(task, agent, _owner, 1, Ct);
        var stale = await service.AssignAsync(task, agent, _owner, 1, Ct);
        var same = await service.AssignAsync(task, agent, _owner, await VersionAsync(task), Ct);

        Assert.Equal(TransitionStatus.Conflict, stale.Status);
        Assert.Equal(TransitionStatus.Refused, same.Status);
        Assert.Single(await InboxAsync(agent));
        Assert.Equal(2, await VersionAsync(task));
    }

    [Fact]
    public async Task The_same_change_enqueued_twice_in_one_transaction_is_a_no_op()
    {
        SkipWithoutDatabase();
        var agent = await AddAgentAsync();
        var task = Guid.CreateVersion7();
        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);

        Assert.True(await AssignmentInbox.EnqueueAsync(tx, task, null, agent, _owner, 2, Ct));
        Assert.False(await AssignmentInbox.EnqueueAsync(tx, task, null, agent, _owner, 2, Ct));
        await tx.CommitAsync(Ct);

        Assert.Single(await InboxAsync(agent));
    }

    [Fact]
    public async Task The_owner_assigning_an_agent_as_that_agent_actor_never_wakes_the_actor()
    {
        SkipWithoutDatabase();
        var agent = await AddAgentAsync();
        var task = await AddTaskAsync();

        // The loop guard in the writer, reached with an actor the authority rule would refuse: call the writer directly.
        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        var wrote = await AssignmentInbox.EnqueueAsync(tx, task, null, agent, new TaskActor(TaskActorKind.Agent, agent), 2, Ct);
        await tx.CommitAsync(Ct);

        Assert.False(wrote);
        Assert.Empty(await InboxAsync(agent));
    }

    [Fact]
    public async Task Claiming_a_ready_card_makes_no_row_for_the_claimer()
    {
        SkipWithoutDatabase();
        var service = new TaskService(db.DataSource);
        var agent = await AddAgentAsync();
        var task = await AddTaskAsync(TaskState.Ready);

        var result = await service.TransitionAsync(task, TaskState.InProgress, new TaskActor(TaskActorKind.Agent, agent), null, 1, Ct);

        Assert.Equal(TransitionStatus.Applied, result.Status);
        Assert.Empty(await InboxAsync(agent));
    }

    [Fact]
    public async Task A_human_or_unknown_assignee_makes_no_row()
    {
        SkipWithoutDatabase();
        var service = new TaskService(db.DataSource);
        var human = Guid.CreateVersion7();
        var task = await AddTaskAsync();

        var result = await service.AssignAsync(task, human, _owner, 1, Ct);

        Assert.Equal(TransitionStatus.Applied, result.Status);
        Assert.Empty(await InboxAsync(human));
    }

    [Fact]
    public async Task A_resting_assignee_gets_an_item_that_accumulates_but_is_not_woken()
    {
        SkipWithoutDatabase();
        var service = new TaskService(db.DataSource);
        var agent = await AddAgentAsync("resting");
        var task = await AddTaskAsync();

        await service.AssignAsync(task, agent, _owner, 1, Ct);

        Assert.Equal(InboxKind.Assignment, Assert.Single(await InboxAsync(agent)).Kind);
        var due = await new InboxRepository(db.DataSource).PeekDueAgentsAsync(1000, Ct);
        Assert.DoesNotContain(due, d => d.AgentId == agent);
    }

    [Fact]
    public async Task An_active_assignee_is_listed_as_due_to_wake()
    {
        SkipWithoutDatabase();
        var service = new TaskService(db.DataSource);
        var agent = await AddAgentAsync();
        var task = await AddTaskAsync();

        await service.AssignAsync(task, agent, _owner, 1, Ct);

        Assert.Contains(await new InboxRepository(db.DataSource).PeekDueAgentsAsync(1000, Ct), d => d.AgentId == agent);
    }

    [Fact]
    public async Task A_retired_assignee_gets_no_row()
    {
        SkipWithoutDatabase();
        var service = new TaskService(db.DataSource);
        var agent = await AddAgentAsync("retired");
        var task = await AddTaskAsync();

        await service.AssignAsync(task, agent, _owner, 1, Ct);

        Assert.Empty(await InboxAsync(agent));
    }

    private async Task<(Guid Task, Guid Gc)> AddTaskWithGcAsync()
    {
        var (gcId, _) = await db.CreateGcWithMessageAsync();
        var id = Guid.CreateVersion7();
        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        await new TaskRepository(db.DataSource).InsertAsync(tx,
            new NewTask(id, Guid.CreateVersion7(), "Task", TaskState.Backlog, null, TaskGate.None, gcId, null, null), Ct);
        await tx.CommitAsync(Ct);
        return (id, gcId);
    }

    private async Task JoinAsync(Guid gc, Guid principal, bool left = false)
    {
        await using var cmd = db.DataSource.CreateCommand(
            "INSERT INTO gc_member (gc_id, principal_id, left_seq) VALUES ($1, $2, $3)");
        cmd.Parameters.AddWithValue(gc);
        cmd.Parameters.AddWithValue(principal);
        cmd.Parameters.AddWithValue(left ? 5L : DBNull.Value);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    [Fact]
    public async Task A_gc_member_agent_may_assign_another_agent_and_the_assignee_is_woken()
    {
        SkipWithoutDatabase();
        var service = new TaskService(db.DataSource);
        var assigner = await AddAgentAsync();
        var target = await AddAgentAsync();
        var (task, gc) = await AddTaskWithGcAsync();
        await JoinAsync(gc, assigner);

        var result = await service.AssignAsync(task, target, new TaskActor(TaskActorKind.Agent, assigner), 1, Ct);

        Assert.Equal(TransitionStatus.Applied, result.Status);
        Assert.Single(await InboxAsync(target));
    }

    [Fact]
    public async Task An_agent_outside_the_gc_or_who_left_it_is_refused_and_nothing_changes()
    {
        SkipWithoutDatabase();
        var service = new TaskService(db.DataSource);
        var outsider = await AddAgentAsync();
        var leaver = await AddAgentAsync();
        var target = await AddAgentAsync();
        var (task, gc) = await AddTaskWithGcAsync();
        await JoinAsync(gc, leaver, left: true);

        var a = await service.AssignAsync(task, target, new TaskActor(TaskActorKind.Agent, outsider), 1, Ct);
        var b = await service.AssignAsync(task, target, new TaskActor(TaskActorKind.Agent, leaver), 1, Ct);

        Assert.Equal(TransitionOutcome.NotAuthorised, a.Outcome);
        Assert.Equal(TransitionOutcome.NotAuthorised, b.Outcome);
        Assert.Equal(TransitionStatus.Refused, a.Status);
        Assert.Empty(await InboxAsync(target));
        Assert.Equal(1, await VersionAsync(task));
    }

    [Fact]
    public async Task An_agent_assigning_itself_must_claim_instead_even_as_a_gc_member()
    {
        SkipWithoutDatabase();
        var service = new TaskService(db.DataSource);
        var agent = await AddAgentAsync();
        var (task, gc) = await AddTaskWithGcAsync();
        await JoinAsync(gc, agent);

        var result = await service.AssignAsync(task, agent, new TaskActor(TaskActorKind.Agent, agent), 1, Ct);

        Assert.Equal(TransitionOutcome.NotAuthorised, result.Outcome);
    }

    [Fact]
    public async Task The_system_actor_may_assign()
    {
        SkipWithoutDatabase();
        var service = new TaskService(db.DataSource);
        var agent = await AddAgentAsync();
        var task = await AddTaskAsync();

        var result = await service.AssignAsync(task, agent, TaskActor.SystemActor, 1, Ct);

        Assert.Equal(TransitionStatus.Applied, result.Status);
        Assert.Single(await InboxAsync(agent));
    }

    [Fact]
    public async Task Unassigning_writes_nothing()
    {
        SkipWithoutDatabase();
        var service = new TaskService(db.DataSource);
        var agent = await AddAgentAsync();
        var task = await AddTaskAsync();
        await service.AssignAsync(task, agent, _owner, 1, Ct);

        var result = await service.AssignAsync(task, null, _owner, 2, Ct);

        Assert.Equal(TransitionStatus.Applied, result.Status);
        Assert.Single(await InboxAsync(agent));
    }
}
