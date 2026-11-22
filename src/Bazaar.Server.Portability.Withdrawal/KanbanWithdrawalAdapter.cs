using System.Globalization;
using Bazaar.RyuseiFuda.Kanban;

namespace Bazaar.Server.Portability.Withdrawal;

/// <summary>
/// Maps <see cref="IKanbanWithdrawalWriter"/> onto the Kanban task service's <see cref="IKanbanTaskWithdrawal"/> port (doc 44
/// section 8.8, doc 12 "Withdrawn state"). The reads come from <see cref="IImportedKanbanTaskReader"/> (<c>tasks.import_job_id</c>
/// and the withdrawal record). Each task is withdrawn by the system actor, so the doc 12 gates apply: a Done task is owner-only
/// and the service refuses it, which fails the batch.
/// </summary>
public sealed class KanbanWithdrawalAdapter(IImportedKanbanTaskReader reader, IKanbanTaskWithdrawal withdrawal) : IKanbanWithdrawalWriter
{
    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ReadImportedTaskIdsAsync(string jobId, CancellationToken ct) => reader.ReadImportedTaskIdsAsync(jobId, ct);

    /// <inheritdoc />
    public Task<IReadOnlySet<string>> ReadWithdrawnTaskIdsAsync(string jobId, CancellationToken ct) => reader.ReadWithdrawnTaskIdsAsync(jobId, ct);

    /// <summary>
    /// Withdraws the tasks one by one. <c>Withdrawn</c> and <c>AlreadyWithdrawn</c> both count as done, so a resumed batch repeats
    /// safely. Any other status throws, so nothing is recorded for the batch and a re-run continues after the tasks already done.
    /// </summary>
    public async Task WithdrawTasksAsync(string jobId, int batchIndex, IReadOnlyList<string> taskIds, string reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentNullException.ThrowIfNull(taskIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        foreach (var text in taskIds)
        {
            ct.ThrowIfCancellationRequested();
            if (!Guid.TryParse(text, out var id))
            {
                throw new InvalidOperationException("Task id is not a GUID: " + text);
            }

            var status = await withdrawal.WithdrawAsync(id, TaskActor.SystemActor, reason, ct);
            if (status is TaskWithdrawalStatus.Withdrawn or TaskWithdrawalStatus.AlreadyWithdrawn)
            {
                continue;
            }
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Kanban refused to withdraw task {text} (batch {batchIndex}): {status}"));
        }
    }
}
