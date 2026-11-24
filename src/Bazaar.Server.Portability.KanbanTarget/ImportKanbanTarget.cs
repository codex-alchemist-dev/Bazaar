using Bazaar.RyuseiFuda.Kanban;
using Bazaar.Server.Portability;

namespace Bazaar.Server.Portability.KanbanTarget;

/// <summary>Writes one GC message per import item into the job's import GC (doc 44 section 8.2, PROPOSED import GC).</summary>
public interface IImportGcSink
{
    /// <summary>Returns the job's import GC, creating it on first use. Named after the source and the time.</summary>
    Task<Guid> EnsureJobGcAsync(string jobId, string sourceName, CancellationToken ct);

    /// <summary>Appends one message to a GC and returns the message id.</summary>
    Task<Guid> AppendMessageAsync(Guid gcId, string text, CancellationToken ct);

    /// <summary>The message texts in the job's import GC, for the committed-key check.</summary>
    Task<IReadOnlyList<string>> ReadJobMessagesAsync(string jobId, CancellationToken ct);
}

/// <summary>Creates a Kanban task from a GC message. The production implementation wraps <c>TaskService</c>.</summary>
public interface IImportTaskStore
{
    /// <summary>Returns the new task id, or null when the store refuses the message.</summary>
    Task<Guid?> CreateFromMessageAsync(Guid projectId, Guid gcId, Guid messageId, string title, string importJobId, CancellationToken ct);

    /// <summary>
    /// Moves a task from Backlog to Ready as the system actor. Returns false when the transition is refused. Only that move is
    /// open to the system actor (doc 12 transition table), so the import does not move a task further.
    /// </summary>
    Task<bool> MoveToReadyAsync(Guid taskId, CancellationToken ct);
}

/// <summary>Kanban target for the import commit path. Each record becomes a GC message, then a task pinned to it.</summary>
public sealed class ImportKanbanTarget(IImportGcSink gcSink, IImportTaskStore tasks, Guid projectId) : IKanbanImportTarget
{
    /// <summary>Marker that starts every import message. The source key after it is how a re-run sees the record is done.</summary>
    public const string MarkerPrefix = "[import:";

    public async Task AppendBatchAsync(string jobId, int batchIndex, IReadOnlyList<ImportItem> items, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) return;

        var gcId = await gcSink.EnsureJobGcAsync(jobId, SourceName(items[0]), ct).ConfigureAwait(false);
        foreach (var item in items)
        {
            var messageId = await gcSink.AppendMessageAsync(gcId, MessageText(item), ct).ConfigureAwait(false);
            var taskId = await tasks.CreateFromMessageAsync(projectId, gcId, messageId, item.Record.Title, jobId, ct).ConfigureAwait(false);
            if (taskId is null)
            {
                throw new InvalidOperationException($"Kanban store refused the task for {item.Record.SourceKey} in batch {batchIndex}.");
            }

            // A refused move leaves the task in Backlog. It does not fail the batch, and the marker still counts as imported.
            if (WantsReady(item)) await tasks.MoveToReadyAsync(taskId.Value, ct).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<string>> ReadSourceKeysAsync(string jobId, CancellationToken ct)
    {
        var messages = await gcSink.ReadJobMessagesAsync(jobId, ct).ConfigureAwait(false);
        var keys = new List<string>();
        foreach (var text in messages)
        {
            if (!text.StartsWith(MarkerPrefix, StringComparison.Ordinal)) continue;
            var end = text.IndexOf(']', MarkerPrefix.Length);
            if (end > MarkerPrefix.Length) keys.Add(text[MarkerPrefix.Length..end]);
        }
        return keys;
    }

    /// <summary>
    /// True when the source column is past Backlog. The system actor may only move a task to Ready, so every such column
    /// is clamped to Ready. The source column stays in the message text.
    /// </summary>
    public static bool WantsReady(ImportItem item) =>
        item.Record.Attributes.TryGetValue("kanban_column", out var column)
        && column is "Ready" or "In progress" or "Review" or "Done";

    /// <summary>Message text: the marker line, the title, then the column the source list had.</summary>
    public static string MessageText(ImportItem item)
    {
        var column = item.Record.Attributes.TryGetValue("kanban_column", out var value) ? value : "Backlog";
        return $"{MarkerPrefix}{item.Record.SourceKey}]\n{item.Record.Title}\nsource column: {column}\n\n{item.Record.Body}";
    }

    private static string SourceName(ImportItem item) =>
        item.Record.Attributes.TryGetValue("board", out var board) && board.Length > 0
            ? board
            : item.Record.Attributes.TryGetValue("source", out var source) ? source : "import";
}

/// <summary>Production task store: pins the task to the message with the system actor, as the Kanban module does for pins.</summary>
public sealed class TaskServiceImportStore(TaskService tasks) : IImportTaskStore
{
    public async Task<Guid?> CreateFromMessageAsync(Guid projectId, Guid gcId, Guid messageId, string title, string importJobId, CancellationToken ct)
    {
        var result = await tasks.CreateFromMessageAsync(projectId, gcId, messageId, title, TaskActor.SystemActor, importJobId, ct).ConfigureAwait(false);
        return result.TaskId;
    }

    public async Task<bool> MoveToReadyAsync(Guid taskId, CancellationToken ct)
    {
        var task = await tasks.GetTaskAsync(taskId, ct).ConfigureAwait(false);
        if (task is null) return false;
        var result = await tasks.TransitionAsync(taskId, TaskState.Ready, TaskActor.SystemActor, "imported", task.Version, ct).ConfigureAwait(false);
        return result.Status == TransitionStatus.Applied;
    }
}
