using Bazaar.RyuseiFuda.Kanban;
using Bazaar.RyuseiFuda.Kanban.Data;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Kanban.Data;

/// <summary>Many parallel callers on the Kanban board: the blocker graph stays acyclic and a card moves at most once per version (doc 12).</summary>
public sealed class KanbanConcurrencyTests(KanbanDb db) : IClassFixture<KanbanDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TaskActor _owner = new(TaskActorKind.Owner, Guid.CreateVersion7());

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

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

    private static bool HasCycle(IReadOnlyList<TaskEdge> edges)
    {
        var next = edges.GroupBy(e => e.BlockerId).ToDictionary(g => g.Key, g => g.Select(e => e.BlockedId).ToList());
        var state = new Dictionary<Guid, int>();

        bool Visit(Guid node)
        {
            state.TryGetValue(node, out var s);
            if (s == 1)
                return true;
            if (s == 2)
                return false;
            state[node] = 1;
            if (next.TryGetValue(node, out var outs) && outs.Any(Visit))
                return true;
            state[node] = 2;
            return false;
        }

        return next.Keys.Any(Visit);
    }

    [Fact]
    public async Task Every_ordered_edge_between_eight_cards_inserted_in_parallel_leaves_an_acyclic_graph()
    {
        SkipWithoutDatabase();
        var (gcId, _) = await db.CreateGcWithMessageAsync();
        var projectId = Guid.CreateVersion7();
        var cards = new List<Guid>();
        for (var i = 0; i < 8; i++)
            cards.Add(await AddTaskAsync(projectId, gcId, TaskState.Backlog));
        var service = new TaskService(db.DataSource);

        var attempts = new List<Task<EdgeResult>>();
        foreach (var blocked in cards)
        {
            foreach (var blocker in cards)
            {
                if (blocked != blocker)
                    attempts.Add(Task.Run(() => service.AddBlockerAsync(blocked, blocker, _owner, Ct), Ct));
            }
        }

        var results = await Task.WhenAll(attempts);

        Assert.DoesNotContain(results, r => r.Status is not (EdgeStatus.Added or EdgeStatus.Cycle or EdgeStatus.TooManyBlockers));
        var edges = await new TaskEdgeRepository(db.DataSource).ListForProjectAsync(projectId, null, Ct);
        Assert.Equal(results.Count(r => r.Status == EdgeStatus.Added), edges.Count);
        Assert.False(HasCycle(edges), "the stored blocker graph has a cycle");
    }

    [Fact]
    public async Task Edge_inserts_racing_edge_removals_keep_the_graph_acyclic()
    {
        SkipWithoutDatabase();
        var (gcId, _) = await db.CreateGcWithMessageAsync();
        var projectId = Guid.CreateVersion7();
        var cards = new List<Guid>();
        for (var i = 0; i < 5; i++)
            cards.Add(await AddTaskAsync(projectId, gcId, TaskState.Backlog));
        var service = new TaskService(db.DataSource);
        for (var i = 0; i < 4; i++)
            await service.AddBlockerAsync(cards[i + 1], cards[i], _owner, Ct);

        // Remove the chain's middle links while the reverse links are attempted.
        var tasks = new List<Task>();
        for (var i = 0; i < 4; i++)
        {
            var blocker = cards[i];
            var blocked = cards[i + 1];
            tasks.Add(Task.Run(() => service.RemoveBlockerAsync(blocked, blocker, _owner, Ct), Ct));
            tasks.Add(Task.Run(() => service.AddBlockerAsync(blocker, blocked, _owner, Ct), Ct));
            tasks.Add(Task.Run(() => service.AddBlockerAsync(cards[0], cards[4], _owner, Ct), Ct));
        }

        await Task.WhenAll(tasks);

        var edges = await new TaskEdgeRepository(db.DataSource).ListForProjectAsync(projectId, null, Ct);
        Assert.False(HasCycle(edges), "the stored blocker graph has a cycle");
    }

    [Fact]
    public async Task Sixteen_claimers_of_one_ready_card_at_one_version_let_one_win_and_write_one_event()
    {
        SkipWithoutDatabase();
        var (gcId, _) = await db.CreateGcWithMessageAsync();
        var card = await AddTaskAsync(Guid.CreateVersion7(), gcId, TaskState.Ready);
        var service = new TaskService(db.DataSource);

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            Task.Run(() => service.TransitionAsync(card, TaskState.InProgress, new TaskActor(TaskActorKind.Agent, Guid.CreateVersion7()), null, 1, Ct), Ct)));

        Assert.Equal(1, results.Count(r => r.Status == TransitionStatus.Applied));
        Assert.Equal(15, results.Count(r => r.Status == TransitionStatus.Conflict));
        await using var cmd = db.DataSource.CreateCommand("SELECT count(*) FROM task_events WHERE task_id = $1 AND kind = $2");
        cmd.Parameters.AddWithValue(card);
        cmd.Parameters.AddWithValue(TaskEventKinds.Transitioned);
        Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync(Ct))!);
    }
}
