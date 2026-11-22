using System.Text.Json;

namespace Bazaar.Server.Portability;

/// <summary>
/// One <c>task_events</c> row in full (doc 12, Events). <see cref="Kind"/> is the stored event kind (for example
/// <c>task.transitioned</c>) and <see cref="PayloadJson"/> is the stored jsonb document. This carries every kind, where
/// <see cref="KanbanTransitionRecord"/> carries only the applied status moves.
/// </summary>
public sealed record WorkdayEventRecord(long Seq, Guid TaskId, string Kind, string Actor, string PayloadJson, DateTimeOffset At);

/// <summary>One <c>task_gate_decisions</c> row (doc 12, Gates). <see cref="Gate"/> is <c>council</c> or <c>owner</c>; <see cref="Outcome"/> is <c>approved</c> or <c>rejected</c>.</summary>
public sealed record WorkdayGateDecisionRecord(
    Guid DecisionId,
    Guid TaskId,
    string Gate,
    string Outcome,
    Guid DecidedBy,
    string Reference,
    DateTimeOffset DecidedAt);

/// <summary>
/// Reads the Workday logs the Kanban exporter does not cover. The host implements it over <c>task_events</c> and
/// <c>task_gate_decisions</c>, for the owner, who alone reads boards (doc 12, doc 44 section 4.y). Every read honours the scope's
/// projects and returns records in the order named here; the exporter refuses a source that breaks it.
/// </summary>
public interface IWorkdayExportSource
{
    /// <summary>Every event of the scope's tasks, in ascending sequence number.</summary>
    IAsyncEnumerable<WorkdayEventRecord> ReadEventsAsync(KanbanExportScope scope, CancellationToken ct);

    /// <summary>Every gate decision of the scope's tasks, in ascending decision id (canonical "D" text, ordinal). Ids are time-ordered uuidv7, so this is also decision order.</summary>
    IAsyncEnumerable<WorkdayGateDecisionRecord> ReadGateDecisionsAsync(KanbanExportScope scope, CancellationToken ct);
}

/// <summary>What a Workday export wrote.</summary>
public sealed record WorkdayExportResult(int EventCount, int GateDecisionCount, IReadOnlyList<string> Files);

/// <summary>
/// Workday exporter (doc 44 section 4.y, PROPOSED; built to the proposal). Tasks, dependency edges, links and the applied status
/// transitions are the Kanban export (section 4.4). This adds the two logs it leaves out: the full <c>task_events</c> log and the
/// <c>task_gate_decisions</c> log, each with its sequence and provenance (actor, time, reference). It is a read.
/// </summary>
/// <remarks>
/// Files: <c>workday/events.jsonl</c>, <c>workday/gate-decisions.jsonl</c> and <c>workday/manifest.json</c>. Event payloads are
/// written as canonical JSON (sorted keys). The logs are append-only and are exported whole, never filtered by kind.
/// </remarks>
public sealed class WorkdayExporter(IWorkdayExportSource source)
{
    /// <summary>The schema version of <c>manifest.json</c>.</summary>
    public const string SchemaVersion = "1.0.0";

    public const string EventsPath = "workday/events.jsonl";
    public const string GateDecisionsPath = "workday/gate-decisions.jsonl";
    public const string ManifestPath = "workday/manifest.json";

    /// <summary>Exports the scope's event and gate-decision logs into <paramref name="sink"/>.</summary>
    public async Task<WorkdayExportResult> ExportAsync(KanbanExportScope scope, IExportFileSink sink, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(sink);

        var files = new List<string>();
        var eventCount = await WriteEventsAsync(scope, sink, files, ct);
        var decisionCount = await WriteDecisionsAsync(scope, sink, files, ct);

        files.Add(ManifestPath);
        await using (var stream = await sink.OpenAsync(ManifestPath, ct))
        {
            using var json = new Utf8JsonWriter(stream);
            json.WriteStartObject();
            json.WriteString("schemaVersion", SchemaVersion);
            json.WriteString("status", "proposed");
            json.WriteNumber("eventCount", eventCount);
            json.WriteNumber("gateDecisionCount", decisionCount);
            json.WriteStartArray("companionFiles");
            foreach (var path in new[] { KanbanExporter.TasksPath, KanbanExporter.EdgesPath, KanbanExporter.TransitionsPath })
            {
                json.WriteStringValue(path);
            }

            json.WriteEndArray();
            json.WriteEndObject();
            await json.FlushAsync(ct);
        }

        return new WorkdayExportResult(eventCount, decisionCount, files);
    }

    private async Task<int> WriteEventsAsync(KanbanExportScope scope, IExportFileSink sink, List<string> files, CancellationToken ct)
    {
        await using var stream = await sink.OpenAsync(EventsPath, ct);
        var writer = new JsonLineWriter(stream);
        files.Add(EventsPath);

        long? previous = null;
        var count = 0;
        await foreach (var item in source.ReadEventsAsync(scope, ct))
        {
            if (previous is { } last && item.Seq <= last)
            {
                throw new InvalidOperationException("Workday source is not in sequence order at seq " + item.Seq + ".");
            }

            previous = item.Seq;
            count++;
            await writer.WriteAsync(
                json =>
                {
                    json.WriteStartObject();
                    json.WriteNumber("seq", item.Seq);
                    json.WriteString("taskId", item.TaskId.ToString("D"));
                    json.WriteString("kind", item.Kind);
                    json.WriteString("actor", item.Actor);
                    json.WriteString("at", ExportText.Timestamp(item.At));
                    CanonicalJson.WriteProperty(json, "payload", item.PayloadJson);
                    json.WriteEndObject();
                },
                ct);
        }

        return count;
    }

    private async Task<int> WriteDecisionsAsync(KanbanExportScope scope, IExportFileSink sink, List<string> files, CancellationToken ct)
    {
        await using var stream = await sink.OpenAsync(GateDecisionsPath, ct);
        var writer = new JsonLineWriter(stream);
        files.Add(GateDecisionsPath);

        string? previous = null;
        var count = 0;
        await foreach (var item in source.ReadGateDecisionsAsync(scope, ct))
        {
            var key = item.DecisionId.ToString("D");
            if (previous is not null && string.CompareOrdinal(previous, key) >= 0)
            {
                throw new InvalidOperationException("Workday source is not in decision id order at " + key + ".");
            }

            if (item.Gate is not ("council" or "owner") || item.Outcome is not ("approved" or "rejected"))
            {
                throw new InvalidOperationException("Gate decision " + key + " has an unknown gate or outcome.");
            }

            previous = key;
            count++;
            await writer.WriteAsync(
                json =>
                {
                    json.WriteStartObject();
                    json.WriteString("decisionId", key);
                    json.WriteString("taskId", item.TaskId.ToString("D"));
                    json.WriteString("gate", item.Gate);
                    json.WriteString("outcome", item.Outcome);
                    json.WriteString("decidedBy", item.DecidedBy.ToString("D"));
                    json.WriteString("reference", item.Reference);
                    json.WriteString("decidedAt", ExportText.Timestamp(item.DecidedAt));
                    json.WriteEndObject();
                },
                ct);
        }

        return count;
    }
}
