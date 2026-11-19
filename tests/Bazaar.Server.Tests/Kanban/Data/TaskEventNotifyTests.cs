using System.Collections.Concurrent;
using Bazaar.Server.Kanban;
using Bazaar.Server.Kanban.Data;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Kanban.Data;

/// <summary>Task events are announced with NOTIFY after commit and never after rollback (doc 12, WP8).</summary>
public sealed class TaskEventNotifyTests(KanbanDb db) : IClassFixture<KanbanDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private async Task<Guid> InsertTaskAsync()
    {
        var (gcId, _) = await db.CreateGcWithMessageAsync();
        var id = Guid.CreateVersion7();
        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        await new TaskRepository(db.DataSource).InsertAsync(tx, new NewTask(id, Guid.CreateVersion7(), "t", TaskState.Backlog, null, TaskGate.None, gcId, null, null), Ct);
        await tx.CommitAsync(Ct);
        return id;
    }

    /// <summary>Opens a LISTEN connection and collects payloads on the kanban channel.</summary>
    private async Task<(NpgsqlConnection Conn, ConcurrentQueue<string> Payloads)> ListenAsync()
    {
        var conn = await db.DataSource.OpenConnectionAsync(Ct);
        var payloads = new ConcurrentQueue<string>();
        conn.Notification += (_, e) => payloads.Enqueue(e.Payload);
        await using (var listen = new NpgsqlCommand($"LISTEN {TaskEventRepository.NotifyChannel}", conn))
            await listen.ExecuteNonQueryAsync(Ct);
        return (conn, payloads);
    }

    /// <summary>Npgsql hands notifications over only while the listening connection is in use, so each poll runs a query on it.</summary>
    private static async Task WaitForAsync(NpgsqlConnection listener, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await using var ping = new NpgsqlCommand("SELECT 1", listener);
            await ping.ExecuteNonQueryAsync(Ct);
            await Task.Delay(20, Ct);
        }
    }

    [Fact]
    public async Task Committed_event_is_announced_once_with_its_seq()
    {
        SkipWithoutDatabase();
        var taskId = await InsertTaskAsync();
        var (listener, payloads) = await ListenAsync();
        await using var _ = listener;
        var repo = new TaskEventRepository(db.DataSource);

        await using (var conn = await db.DataSource.OpenConnectionAsync(Ct))
        await using (var tx = await conn.BeginTransactionAsync(Ct))
        {
            var seq = await repo.AppendAsync(tx, new NewTaskEvent(taskId, TaskEventKinds.Created, "system", "{}"), Ct);
            Assert.Empty(payloads);
            await tx.CommitAsync(Ct);
            await WaitForAsync(listener, () => !payloads.IsEmpty);
            Assert.Single(payloads);
            Assert.Contains($"\"seq\":{seq}", payloads.Single());
            Assert.Contains(taskId.ToString("D"), payloads.Single());
        }
    }

    [Fact]
    public async Task Rolled_back_event_is_never_announced()
    {
        SkipWithoutDatabase();
        var taskId = await InsertTaskAsync();
        var (listener, payloads) = await ListenAsync();
        await using var _ = listener;
        var repo = new TaskEventRepository(db.DataSource);

        await using (var conn = await db.DataSource.OpenConnectionAsync(Ct))
        {
            await using (var tx = await conn.BeginTransactionAsync(Ct))
            {
                await repo.AppendAsync(tx, new NewTaskEvent(taskId, TaskEventKinds.Created, "system", "{}"), Ct);
                await tx.RollbackAsync(Ct);
            }

            // A committed marker after the rolled-back one proves the listener is live and the rollback was not delivered.
            await using (var tx = await conn.BeginTransactionAsync(Ct))
            {
                await repo.AppendAsync(tx, new NewTaskEvent(taskId, TaskEventKinds.Transitioned, "system", "{}"), Ct);
                await tx.CommitAsync(Ct);
            }
        }

        await WaitForAsync(listener, () => !payloads.IsEmpty);
        await WaitForAsync(listener, () => false);
        Assert.Single(payloads);
        Assert.Contains("\"kind\":\"task.transitioned\"", payloads.Single());
    }
}
