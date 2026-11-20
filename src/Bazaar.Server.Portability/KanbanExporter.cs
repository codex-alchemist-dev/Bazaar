using System.Globalization;
using System.Text.Json;

namespace Bazaar.Server.Portability;

/// <summary>What a Kanban export wrote: the row counts for the audit entry (doc 44 section 4.1) and the files it created.</summary>
public sealed record KanbanExportResult(int TaskCount, int EdgeCount, int TransitionCount, IReadOnlyList<string> Files);

/// <summary>
/// Kanban exporter (doc 44 section 4.4). Streams the boards, tasks, dependency edges and transitions as bundle files. It is a read:
/// it never changes the source.
/// </summary>
/// <remarks>
/// Files: <c>kanban/boards.json</c>, <c>kanban/tasks.jsonl</c>, <c>kanban/tasks.csv</c> (a derived table, never imported back),
/// <c>kanban/edges.jsonl</c> and <c>kanban/transitions.jsonl</c>. Only an edge whose two ends are both exported is written. A
/// transition is written only for an exported task. Blocked is written as a derived flag with its reason (<c>blockedBy</c>).
/// Task links are written with each task (doc 44 section 4.4 does not list them; this exporter includes them, see the report).
/// </remarks>
public sealed class KanbanExporter(IKanbanExportSource source)
{
    /// <summary>The schema version of <c>boards.json</c> (doc 44 section 4.1).</summary>
    public const string SchemaVersion = "1.0.0";

    public const string BoardsPath = "kanban/boards.json";
    public const string TasksPath = "kanban/tasks.jsonl";
    public const string TasksCsvPath = "kanban/tasks.csv";
    public const string EdgesPath = "kanban/edges.jsonl";
    public const string TransitionsPath = "kanban/transitions.jsonl";

    private const string _csvHeader = "id,project_id,title,column,owner_agent_id,gate,blocked,version,created_at,updated_at";

    private static readonly string[] _columnsInOrder =
    [
        ColumnText(KanbanColumn.Backlog),
        ColumnText(KanbanColumn.Ready),
        ColumnText(KanbanColumn.InProgress),
        ColumnText(KanbanColumn.Review),
        ColumnText(KanbanColumn.Done),
    ];

    /// <summary>Exports the scope's boards, tasks, edges and transitions into <paramref name="sink"/>.</summary>
    public async Task<KanbanExportResult> ExportAsync(KanbanExportScope scope, IExportFileSink sink, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(sink);

        var files = new List<string>();
        var exportedTasks = new HashSet<Guid>();
        var taskCounts = new Dictionary<Guid, int>();
        var taskCount = await WriteTasksAsync(scope, sink, files, exportedTasks, taskCounts, ct);
        var edgeCount = await WriteEdgesAsync(scope, sink, files, exportedTasks, ct);
        var transitionCount = await WriteTransitionsAsync(scope, sink, files, exportedTasks, ct);
        await WriteBoardsAsync(sink, files, taskCounts, ct);

        return new KanbanExportResult(taskCount, edgeCount, transitionCount, files);
    }

    private async Task<int> WriteTasksAsync(
        KanbanExportScope scope,
        IExportFileSink sink,
        List<string> files,
        HashSet<Guid> exportedTasks,
        Dictionary<Guid, int> taskCounts,
        CancellationToken ct)
    {
        await using var tasksStream = await sink.OpenAsync(TasksPath, ct);
        await using var csvStream = await sink.OpenAsync(TasksCsvPath, ct);
        await using var csv = new StreamWriter(csvStream, ExportText.Utf8, leaveOpen: true) { NewLine = "\n" };
        var tasks = new JsonLineWriter(tasksStream);
        files.Add(TasksPath);
        files.Add(TasksCsvPath);

        csv.WriteLine(_csvHeader);
        string? previous = null;
        var count = 0;
        await foreach (var task in source.ReadTasksAsync(scope, ct))
        {
            var key = task.Id.ToString("D");
            if (previous is not null && string.CompareOrdinal(previous, key) >= 0)
            {
                throw new InvalidOperationException("Kanban source is not in ascending id order at task " + key + ".");
            }

            previous = key;
            exportedTasks.Add(task.Id);
            taskCounts[task.ProjectId] = taskCounts.GetValueOrDefault(task.ProjectId) + 1;
            count++;

            await tasks.WriteAsync(json => WriteTask(json, task), ct);
            WriteCsvRow(csv, task);
        }

        return count;
    }

    private async Task<int> WriteEdgesAsync(
        KanbanExportScope scope,
        IExportFileSink sink,
        List<string> files,
        HashSet<Guid> exportedTasks,
        CancellationToken ct)
    {
        await using var stream = await sink.OpenAsync(EdgesPath, ct);
        var edges = new JsonLineWriter(stream);
        files.Add(EdgesPath);

        (string Blocker, string Blocked)? previous = null;
        var count = 0;
        await foreach (var edge in source.ReadEdgesAsync(scope, ct))
        {
            var key = (edge.BlockerId.ToString("D"), edge.BlockedId.ToString("D"));
            if (previous is { } last && ComparePairs(last, key) >= 0)
            {
                throw new InvalidOperationException("Kanban source is not in edge order at " + key.Item1 + " to " + key.Item2 + ".");
            }

            previous = key;
            // Both ends must be exported. An edge that reaches a task outside the scope names data the export does not carry.
            if (!exportedTasks.Contains(edge.BlockerId) || !exportedTasks.Contains(edge.BlockedId))
            {
                continue;
            }

            count++;
            await edges.WriteAsync(json => WriteEdge(json, edge), ct);
        }

        return count;
    }

    private async Task<int> WriteTransitionsAsync(
        KanbanExportScope scope,
        IExportFileSink sink,
        List<string> files,
        HashSet<Guid> exportedTasks,
        CancellationToken ct)
    {
        await using var stream = await sink.OpenAsync(TransitionsPath, ct);
        var transitions = new JsonLineWriter(stream);
        files.Add(TransitionsPath);

        long? previous = null;
        var count = 0;
        await foreach (var transition in source.ReadTransitionsAsync(scope, ct))
        {
            if (previous is { } last && transition.Seq <= last)
            {
                throw new InvalidOperationException("Kanban source is not in sequence order at seq " + transition.Seq + ".");
            }

            previous = transition.Seq;
            if (!exportedTasks.Contains(transition.TaskId))
            {
                continue;
            }

            count++;
            await transitions.WriteAsync(json => WriteTransition(json, transition), ct);
        }

        return count;
    }

    private static async Task WriteBoardsAsync(
        IExportFileSink sink,
        List<string> files,
        Dictionary<Guid, int> taskCounts,
        CancellationToken ct)
    {
        await using var stream = await sink.OpenAsync(BoardsPath, ct);
        files.Add(BoardsPath);

        // The boards are the distinct projects of the exported tasks, in ascending project id order.
        var projects = taskCounts.Keys.OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToList();
        using var json = new Utf8JsonWriter(stream);
        json.WriteStartObject();
        json.WriteString("schemaVersion", SchemaVersion);
        json.WriteStartArray("columns");
        foreach (var column in _columnsInOrder)
        {
            json.WriteStringValue(column);
        }

        json.WriteEndArray();
        json.WriteStartArray("derived");
        json.WriteStringValue("blocked");
        json.WriteEndArray();
        json.WriteStartArray("boards");
        foreach (var project in projects)
        {
            json.WriteStartObject();
            json.WriteString("projectId", project.ToString("D"));
            json.WriteNumber("taskCount", taskCounts[project]);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
        await json.FlushAsync(ct);
    }

    private static void WriteTask(Utf8JsonWriter json, KanbanTaskRecord task)
    {
        json.WriteStartObject();
        json.WriteString("id", task.Id.ToString("D"));
        json.WriteString("projectId", task.ProjectId.ToString("D"));
        json.WriteString("title", task.Title);
        json.WriteString("column", ColumnText(task.Column));
        WriteOptional(json, "ownerAgentId", task.OwnerAgentId?.ToString("D"));
        json.WriteString("gate", GateText(task.Gate));
        json.WriteString("originGcId", task.OriginGcId.ToString("D"));
        WriteOptional(json, "originMessageId", task.OriginMessageId?.ToString("D"));
        WriteOptional(json, "dueAt", task.DueAt is { } due ? ExportText.Timestamp(due) : null);
        WriteOptional(json, "reviewApprovedBy", task.ReviewApprovedBy?.ToString("D"));
        WriteOptional(json, "reviewApprovedAt", task.ReviewApprovedAt is { } approved ? ExportText.Timestamp(approved) : null);
        json.WriteNumber("version", task.Version);
        json.WriteString("createdAt", ExportText.Timestamp(task.CreatedAt));
        json.WriteString("updatedAt", ExportText.Timestamp(task.UpdatedAt));

        // Derived: blocked is true while any blocker is not Done. blockedBy is the reason, the unfinished blockers.
        var blockers = task.UnfinishedBlockers.Select(id => id.ToString("D")).Order(StringComparer.Ordinal).ToList();
        json.WriteBoolean("blocked", blockers.Count > 0);
        json.WriteStartArray("blockedBy");
        foreach (var blocker in blockers)
        {
            json.WriteStringValue(blocker);
        }

        json.WriteEndArray();

        json.WriteStartArray("links");
        foreach (var link in task.Links.OrderBy(l => LinkText(l.Kind), StringComparer.Ordinal).ThenBy(l => l.Ref, StringComparer.Ordinal))
        {
            json.WriteStartObject();
            json.WriteString("kind", LinkText(link.Kind));
            json.WriteString("ref", link.Ref);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void WriteEdge(Utf8JsonWriter json, KanbanEdgeRecord edge)
    {
        json.WriteStartObject();
        json.WriteString("from", edge.BlockerId.ToString("D"));
        json.WriteString("to", edge.BlockedId.ToString("D"));
        json.WriteString("kind", "blocks");
        json.WriteString("createdBy", edge.CreatedBy);
        json.WriteString("createdAt", ExportText.Timestamp(edge.CreatedAt));
        json.WriteEndObject();
    }

    private static void WriteTransition(Utf8JsonWriter json, KanbanTransitionRecord transition)
    {
        json.WriteStartObject();
        json.WriteNumber("seq", transition.Seq);
        json.WriteString("taskId", transition.TaskId.ToString("D"));
        json.WriteString("from", ColumnText(transition.From));
        json.WriteString("to", ColumnText(transition.To));
        json.WriteString("actor", transition.Actor);
        json.WriteString("at", ExportText.Timestamp(transition.At));
        WriteOptional(json, "reason", transition.Reason);
        json.WriteString("gate", GateText(transition.Gate));
        json.WriteEndObject();
    }

    private static void WriteCsvRow(StreamWriter csv, KanbanTaskRecord task)
    {
        var blocked = task.UnfinishedBlockers.Count > 0 ? "true" : "false";
        var fields = new[]
        {
            task.Id.ToString("D"),
            task.ProjectId.ToString("D"),
            CsvField(task.Title),
            ColumnText(task.Column),
            task.OwnerAgentId?.ToString("D") ?? string.Empty,
            GateText(task.Gate),
            blocked,
            task.Version.ToString(CultureInfo.InvariantCulture),
            ExportText.Timestamp(task.CreatedAt),
            ExportText.Timestamp(task.UpdatedAt),
        };
        csv.WriteLine(string.Join(',', fields));
    }

    /// <summary>
    /// RFC 4180 quoting. A field with a comma, quote or line break is quoted, and its quotes are doubled. A field that a spreadsheet
    /// would run as a formula (leading =, +, -, @, tab or carriage return) gets a leading apostrophe, so opening the table does not run
    /// the text. The table is derived and never imported back, so the apostrophe does not change the source.
    /// </summary>
    internal static string CsvField(string value)
    {
        var formula = value.Length > 0 && (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r');
        var text = formula ? "'" + value : value;
        if (text.IndexOfAny([',', '"', '\n', '\r']) < 0)
        {
            return text;
        }

        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static void WriteOptional(Utf8JsonWriter json, string name, string? value)
    {
        if (value is null)
        {
            json.WriteNull(name);
        }
        else
        {
            json.WriteString(name, value);
        }
    }

    private static int ComparePairs((string Blocker, string Blocked) left, (string Blocker, string Blocked) right)
    {
        var byBlocker = string.CompareOrdinal(left.Blocker, right.Blocker);
        return byBlocker != 0 ? byBlocker : string.CompareOrdinal(left.Blocked, right.Blocked);
    }

    private static string ColumnText(KanbanColumn column) => column switch
    {
        KanbanColumn.Backlog => "backlog",
        KanbanColumn.Ready => "ready",
        KanbanColumn.InProgress => "in_progress",
        KanbanColumn.Review => "review",
        KanbanColumn.Done => "done",
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Unknown Kanban column."),
    };

    private static string GateText(KanbanGate gate) => gate switch
    {
        KanbanGate.None => "none",
        KanbanGate.Council => "council",
        KanbanGate.Owner => "owner",
        _ => throw new ArgumentOutOfRangeException(nameof(gate), gate, "Unknown Kanban gate."),
    };

    private static string LinkText(KanbanLinkKind kind) => kind switch
    {
        KanbanLinkKind.Pr => "pr",
        KanbanLinkKind.Commit => "commit",
        KanbanLinkKind.Gc => "gc",
        KanbanLinkKind.VaultDoc => "vault_doc",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown Kanban link kind."),
    };
}
