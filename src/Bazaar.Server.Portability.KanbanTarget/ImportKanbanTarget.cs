using Bazaar.Server.Portability;
using Bazaar.RyuseiFuda.Kanban;

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
    Task<Guid?> CreateFromMessageAsync(Guid projectId, Guid gcId, Guid messageId, string title, CancellationToken ct);
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
            var taskId = await tasks.CreateFromMessageAsync(projectId, gcId, messageId, item.Record.Title, ct).ConfigureAwait(false);
            if (taskId is null)
            {
                throw new InvalidOperationException($"Kanban store refused the task for {item.Record.SourceKey} in batch {batchIndex}.");
            }
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
    public async Task<Guid?> CreateFromMessageAsync(Guid projectId, Guid gcId, Guid messageId, string title, CancellationToken ct)
    {
        var result = await tasks.CreateFromMessageAsync(projectId, gcId, messageId, title, TaskActor.SystemActor, ct).ConfigureAwait(false);
        return result.TaskId;
    }
}
