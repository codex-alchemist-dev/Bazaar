using System.Text.Json;

namespace Bazaar.Server.Portability;

/// <summary>
/// One <c>ryusei_trigger</c> row, every stored column (doc 38, Data model). <see cref="Kind"/>, <see cref="PingMode"/> and
/// <see cref="State"/> are the stored text. <see cref="SpecJson"/> is the stored jsonb spec.
/// </summary>
public sealed record OrreryTriggerRecord(
    Guid TriggerId,
    Guid ProjectId,
    Guid CreatedBy,
    string Kind,
    IReadOnlyList<Guid> TargetAgentIds,
    string Purpose,
    string SpecJson,
    Guid? TaskId,
    Guid? AttachGcId,
    string PingMode,
    string State,
    DateTimeOffset? NextFireAt,
    long FireCount,
    int ConsecutiveRefusals,
    long? MaxFires,
    DateTimeOffset? ExpiresAt,
    int ChainDepth,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>One <c>ryusei_fire</c> row (doc 38): the append-only journal of every fire, including refusals and skips.</summary>
public sealed record OrreryFireRecord(
    Guid FireId,
    Guid TriggerId,
    DateTimeOffset ScheduledFor,
    string Outcome,
    string? CauseJson,
    long? GcSeq,
    DateTimeOffset At);

/// <summary>
/// Reads Orrery data for the exporter. The host implements it over <c>ryusei_trigger</c> and <c>ryusei_fire</c>, for the owner.
/// Both reads honour the scope's projects (a fire follows its trigger's project) and return the order named here.
/// </summary>
public interface IOrreryExportSource
{
    /// <summary>Triggers in ascending trigger id (canonical "D" text, ordinal).</summary>
    IAsyncEnumerable<OrreryTriggerRecord> ReadTriggersAsync(KanbanExportScope scope, CancellationToken ct);

    /// <summary>Fires in ascending fire id (canonical "D" text, ordinal). Ids are time-ordered uuidv7.</summary>
    IAsyncEnumerable<OrreryFireRecord> ReadFiresAsync(KanbanExportScope scope, CancellationToken ct);
}

/// <summary>What an Orrery export wrote.</summary>
public sealed record OrreryExportResult(int TriggerCount, int FireCount, IReadOnlyList<string> Files);

/// <summary>
/// Orrery exporter (doc 44 section 4.y, PROPOSED; built to the proposal). Writes trigger definitions as rows and the fire journal.
/// Every trigger line carries <c>restoreAs: "paused"</c>: an import restores definitions paused, so nothing fires on import. It is
/// a read.
/// </summary>
/// <remarks>
/// Files: <c>orrery/triggers.jsonl</c>, <c>orrery/fires.jsonl</c> and <c>orrery/manifest.json</c>. A fire is written only for an
/// exported trigger. The security record of each fire (audit kinds <c>ryusei.trigger.*</c>) is part of the audit-log export
/// (section 4.6) and points at the fire by its id, so it is not copied here.
/// </remarks>
public sealed class OrreryExporter(IOrreryExportSource source)
{
    /// <summary>The schema version of <c>manifest.json</c>.</summary>
    public const string SchemaVersion = "1.0.0";

    public const string TriggersPath = "orrery/triggers.jsonl";
    public const string FiresPath = "orrery/fires.jsonl";
    public const string ManifestPath = "orrery/manifest.json";

    /// <summary>The state an import gives every restored trigger (doc 44 section 4.y).</summary>
    public const string RestoreState = "paused";

    private static readonly HashSet<string> _kinds = new(StringComparer.Ordinal) { "timer", "interval", "watcher", "event" };
    private static readonly HashSet<string> _pingModes = new(StringComparer.Ordinal) { "none", "attach", "new_gc" };
    private static readonly HashSet<string> _states = new(StringComparer.Ordinal) { "active", "paused", "fired", "expired", "cancelled" };

    /// <summary>Exports the scope's trigger definitions and fire journal into <paramref name="sink"/>.</summary>
    public async Task<OrreryExportResult> ExportAsync(KanbanExportScope scope, IExportFileSink sink, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(sink);

        var files = new List<string>();
        var exported = new HashSet<Guid>();
        var triggerCount = await WriteTriggersAsync(scope, sink, files, exported, ct);
        var fireCount = await WriteFiresAsync(scope, sink, files, exported, ct);

        files.Add(ManifestPath);
        await using (var stream = await sink.OpenAsync(ManifestPath, ct))
        {
            using var json = new Utf8JsonWriter(stream);
            json.WriteStartObject();
            json.WriteString("schemaVersion", SchemaVersion);
            json.WriteString("status", "proposed");
            json.WriteString("restoreAs", RestoreState);
            json.WriteNumber("triggerCount", triggerCount);
            json.WriteNumber("fireCount", fireCount);
            json.WriteEndObject();
            await json.FlushAsync(ct);
        }

        return new OrreryExportResult(triggerCount, fireCount, files);
    }

    private async Task<int> WriteTriggersAsync(
        KanbanExportScope scope,
        IExportFileSink sink,
        List<string> files,
        HashSet<Guid> exported,
        CancellationToken ct)
    {
        await using var stream = await sink.OpenAsync(TriggersPath, ct);
        var writer = new JsonLineWriter(stream);
        files.Add(TriggersPath);

        string? previous = null;
        var count = 0;
        await foreach (var t in source.ReadTriggersAsync(scope, ct))
        {
            var key = t.TriggerId.ToString("D");
            if (previous is not null && string.CompareOrdinal(previous, key) >= 0)
            {
                throw new InvalidOperationException("Orrery source is not in trigger id order at " + key + ".");
            }

            if (!_kinds.Contains(t.Kind) || !_pingModes.Contains(t.PingMode) || !_states.Contains(t.State))
            {
                throw new InvalidOperationException("Trigger " + key + " has an unknown kind, ping mode or state.");
            }

            previous = key;
            exported.Add(t.TriggerId);
            count++;
            await writer.WriteAsync(json => WriteTrigger(json, t, key), ct);
        }

        return count;
    }

    private async Task<int> WriteFiresAsync(
        KanbanExportScope scope,
        IExportFileSink sink,
        List<string> files,
        HashSet<Guid> exported,
        CancellationToken ct)
    {
        await using var stream = await sink.OpenAsync(FiresPath, ct);
        var writer = new JsonLineWriter(stream);
        files.Add(FiresPath);

        string? previous = null;
        var count = 0;
        await foreach (var f in source.ReadFiresAsync(scope, ct))
        {
            var key = f.FireId.ToString("D");
            if (previous is not null && string.CompareOrdinal(previous, key) >= 0)
            {
                throw new InvalidOperationException("Orrery source is not in fire id order at " + key + ".");
            }

            previous = key;
            if (!exported.Contains(f.TriggerId))
            {
                continue;
            }

            count++;
            await writer.WriteAsync(
                json =>
                {
                    json.WriteStartObject();
                    json.WriteString("fireId", key);
                    json.WriteString("triggerId", f.TriggerId.ToString("D"));
                    json.WriteString("scheduledFor", ExportText.Timestamp(f.ScheduledFor));
                    json.WriteString("outcome", f.Outcome);
                    CanonicalJson.WriteProperty(json, "cause", f.CauseJson);
                    if (f.GcSeq is { } seq)
                    {
                        json.WriteNumber("gcSeq", seq);
                    }
                    else
                    {
                        json.WriteNull("gcSeq");
                    }

                    json.WriteString("at", ExportText.Timestamp(f.At));
                    json.WriteEndObject();
                },
                ct);
        }

        return count;
    }

    private static void WriteTrigger(Utf8JsonWriter json, OrreryTriggerRecord t, string key)
    {
        json.WriteStartObject();
        json.WriteString("triggerId", key);
        json.WriteString("projectId", t.ProjectId.ToString("D"));
        json.WriteString("createdBy", t.CreatedBy.ToString("D"));
        json.WriteString("kind", t.Kind);
        json.WriteStartArray("targetAgentIds");
        foreach (var id in t.TargetAgentIds.Select(i => i.ToString("D")).Order(StringComparer.Ordinal))
        {
            json.WriteStringValue(id);
        }

        json.WriteEndArray();
        json.WriteString("purpose", t.Purpose);
        CanonicalJson.WriteProperty(json, "spec", t.SpecJson);
        WriteOptional(json, "taskId", t.TaskId?.ToString("D"));
        WriteOptional(json, "attachGcId", t.AttachGcId?.ToString("D"));
        json.WriteString("pingMode", t.PingMode);
        json.WriteString("state", t.State);
        WriteOptional(json, "nextFireAt", t.NextFireAt is { } next ? ExportText.Timestamp(next) : null);
        json.WriteNumber("fireCount", t.FireCount);
        json.WriteNumber("consecutiveRefusals", t.ConsecutiveRefusals);
        if (t.MaxFires is { } max)
        {
            json.WriteNumber("maxFires", max);
        }
        else
        {
            json.WriteNull("maxFires");
        }

        WriteOptional(json, "expiresAt", t.ExpiresAt is { } expires ? ExportText.Timestamp(expires) : null);
        json.WriteNumber("chainDepth", t.ChainDepth);
        json.WriteNumber("version", t.Version);
        json.WriteString("createdAt", ExportText.Timestamp(t.CreatedAt));
        json.WriteString("updatedAt", ExportText.Timestamp(t.UpdatedAt));
        json.WriteString("restoreAs", RestoreState);
        json.WriteEndObject();
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
}
