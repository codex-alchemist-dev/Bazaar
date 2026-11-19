using System.Text.Json;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.Kanban;
using Bazaar.Server.Kanban.Data;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Kanban.Data;

/// <summary>The <c>kanban.transition</c> agent tool over the real task service (doc 12, Interception).</summary>
public sealed class KanbanTransitionToolTests(KanbanDb db) : IClassFixture<KanbanDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private async Task<(Guid ProjectId, Guid GcId)> NewBoardAsync()
    {
        var (gcId, _) = await db.CreateGcWithMessageAsync();
        return (Guid.CreateVersion7(), gcId);
    }

    private async Task<Guid> AddTaskAsync(Guid projectId, Guid gcId, TaskState state, Guid? owner = null)
    {
        var id = Guid.CreateVersion7();
        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        await new TaskRepository(db.DataSource).InsertAsync(tx,
            new NewTask(id, projectId, "Task", state, owner, TaskGate.None, gcId, null, null), Ct);
        await tx.CommitAsync(Ct);
        return id;
    }

    private static JsonElement Args(object value) =>
        JsonElement.Parse(JsonSerializer.Serialize(value));

    private static async Task<JsonElement> RunAsync(KanbanTransitionTool tool, Guid agent, object args)
    {
        var text = await tool.ExecuteAsync(Args(args), new ToolCallContext(agent, Guid.CreateVersion7(), Guid.CreateVersion7()), Ct);
        return JsonElement.Parse(text);
    }

    [Fact]
    public async Task Assignee_claims_a_ready_card_and_the_version_moves()
    {
        SkipWithoutDatabase();
        var (project, gc) = await NewBoardAsync();
        var agent = Guid.CreateVersion7();
        var task = await AddTaskAsync(project, gc, TaskState.Ready);
        var tool = new KanbanTransitionTool(new TaskService(db.DataSource));

        var reply = await RunAsync(tool, agent, new { taskId = task, to = "in_progress", expectedVersion = 1 });

        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("applied", reply.GetProperty("status").GetString());
        Assert.Equal(2, reply.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task A_stale_version_answers_conflict_and_changes_nothing()
    {
        SkipWithoutDatabase();
        var (project, gc) = await NewBoardAsync();
        var task = await AddTaskAsync(project, gc, TaskState.Ready);
        var tool = new KanbanTransitionTool(new TaskService(db.DataSource));

        var reply = await RunAsync(tool, Guid.CreateVersion7(), new { taskId = task, to = "in_progress", expectedVersion = 7 });

        Assert.Equal("conflict", reply.GetProperty("status").GetString());
        Assert.Equal(1, reply.GetProperty("currentVersion").GetInt64());
    }

    [Fact]
    public async Task Starting_a_card_with_an_unfinished_blocker_is_refused_with_the_blocker_ids()
    {
        SkipWithoutDatabase();
        var (project, gc) = await NewBoardAsync();
        var service = new TaskService(db.DataSource);
        var blocked = await AddTaskAsync(project, gc, TaskState.Ready);
        var blocker = await AddTaskAsync(project, gc, TaskState.InProgress);
        await service.AddBlockerAsync(blocked, blocker, new TaskActor(TaskActorKind.Owner, Guid.CreateVersion7()), Ct);
        var tool = new KanbanTransitionTool(service);

        var reply = await RunAsync(tool, Guid.CreateVersion7(), new { taskId = blocked, to = "in_progress", expectedVersion = 1 });

        Assert.Equal("refused", reply.GetProperty("status").GetString());
        Assert.Equal("blockers_unfinished", reply.GetProperty("outcome").GetString());
        Assert.Equal(blocker.ToString("D"), reply.GetProperty("unfinishedBlockers")[0].GetString());
    }

    [Fact]
    public async Task The_actor_is_the_calling_agent_so_a_non_assignee_cannot_move_to_done()
    {
        SkipWithoutDatabase();
        var (project, gc) = await NewBoardAsync();
        var assignee = Guid.CreateVersion7();
        var task = await AddTaskAsync(project, gc, TaskState.Review, owner: assignee);
        var tool = new KanbanTransitionTool(new TaskService(db.DataSource));

        // Review to Done is allowed for a reviewer only; the assignee calling it is the same agent the context names.
        var reply = await RunAsync(tool, assignee, new { taskId = task, to = "done", expectedVersion = 1, reason = "self-approved" });

        Assert.Equal("refused", reply.GetProperty("status").GetString());
        Assert.Equal("not_authorised", reply.GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData("{\"taskId\":\"nope\",\"to\":\"done\",\"expectedVersion\":1}")]
    [InlineData("{\"taskId\":\"00000000-0000-0000-0000-000000000001\",\"to\":\"blocked\",\"expectedVersion\":1}")]
    [InlineData("{\"taskId\":\"00000000-0000-0000-0000-000000000001\",\"to\":\"done\",\"expectedVersion\":0}")]
    [InlineData("[]")]
    public async Task Malformed_arguments_are_refused_before_any_database_call(string json)
    {
        var tool = new KanbanTransitionTool(new TaskService(new Npgsql.NpgsqlDataSourceBuilder("Host=127.0.0.1;Port=1").Build()));
        var text = await tool.ExecuteAsync(JsonElement.Parse(json),
            new ToolCallContext(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()), Ct);

        Assert.Equal("invalid_arguments", JsonElement.Parse(text).GetProperty("status").GetString());
    }
}
