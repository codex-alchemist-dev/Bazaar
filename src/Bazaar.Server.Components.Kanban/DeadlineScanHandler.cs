using System.Globalization;
using System.Text.Json.Nodes;
using Bazaar.Server.Jobs;
using Bazaar.Server.Kanban.Data;
using Npgsql;

namespace Bazaar.Server.Kanban;

/// <summary>
/// Runs the <c>kanban.deadline_scan</c> job (doc 12, WP11). Each run appends <c>task.due_passed</c> once for every (task, due_at)
/// pair that has passed, with the due time in the payload. The scheduler that enqueues this job belongs to doc 09 and is not here.
/// <para>
/// Uniqueness: there is no partial unique index, because a migration needs owner review. Instead, each candidate is handled in
/// its own transaction. The transaction locks the task row with <c>SELECT ... FOR UPDATE</c>, then re-checks the due-passed
/// condition and the absence of a matching event in a new statement, which sees any event a concurrent scanner committed while
/// this one waited for the lock. Only then does it append. Two runs therefore cannot both emit for the same pair, and a retry
/// after a crash finds the event already there.
/// </para>
/// <para>
/// Hard mode uses the same event scan; the task service enforces the hard start guard when a caller tries to claim a past-due
/// task. The scanner remains responsible only for recording that the due time passed.
/// </para>
/// </summary>
public sealed class DeadlineScanHandler(
    NpgsqlDataSource dataSource,
    TaskDeadlineRepository deadlines,
    TaskRepository tasks,
    TaskEventRepository events,
    DeadlineScanOptions options) : IJobHandler
{
    /// <summary>The job kind served by this handler.</summary>
    public const string JobKind = "kanban.deadline_scan";

    public string Kind => JobKind;

    public async Task HandleAsync(JobRecord job, CancellationToken ct) => await ScanAsync(ct).ConfigureAwait(false);

    /// <summary>Runs one bounded pass and returns how many <c>task.due_passed</c> events it appended.</summary>
    public async Task<int> ScanAsync(CancellationToken ct = default)
    {
        var emitted = 0;
        foreach (var taskId in await deadlines.ListCandidatesAsync(options.BatchSize, ct).ConfigureAwait(false))
        {
            if (await EmitIfStillDueAsync(taskId, ct).ConfigureAwait(false))
            {
                emitted++;
            }
        }

        return emitted;
    }

    private async Task<bool> EmitIfStillDueAsync(Guid taskId, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Lock the row first. The task may have moved to Done or changed its due time since the candidate read.
        if (await tasks.GetForUpdateAsync(tx, taskId, ct).ConfigureAwait(false) is null)
        {
            return false;
        }

        if (await deadlines.GetUnnotifiedDueAtAsync(tx, taskId, ct).ConfigureAwait(false) is not { } dueAt)
        {
            return false;
        }

        var payload = new JsonObject { ["due_at"] = dueAt.ToString("O", CultureInfo.InvariantCulture) };
        await events.AppendAsync(tx, new NewTaskEvent(taskId, TaskEventKinds.DuePassed, TaskActor.SystemActor.ToText(),
            payload.ToJsonString()), ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }
}
