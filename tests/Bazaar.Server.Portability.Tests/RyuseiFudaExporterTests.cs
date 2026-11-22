using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Bazaar.Server.Portability.Tests;

public class RyuseiFudaExporterTests
{
    private static readonly DateTimeOffset _at = new(2026, 11, 18, 9, 30, 0, TimeSpan.Zero);
    private static readonly Guid _projectA = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid _projectB = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid _task = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid _agent = Guid.Parse("40000000-0000-0000-0000-000000000001");

    private static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");

    private static async IAsyncEnumerable<T> Stream<T>(IEnumerable<T> rows, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var row in rows.ToList())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return row;
        }
    }

    // ---- Workday ----

    private sealed class FakeWorkday : IWorkdayExportSource
    {
        public List<WorkdayEventRecord> Events { get; } = [];

        public List<WorkdayGateDecisionRecord> Decisions { get; } = [];

        public IAsyncEnumerable<WorkdayEventRecord> ReadEventsAsync(KanbanExportScope scope, CancellationToken ct) => Stream(Events, ct);

        public IAsyncEnumerable<WorkdayGateDecisionRecord> ReadGateDecisionsAsync(KanbanExportScope scope, CancellationToken ct) => Stream(Decisions, ct);
    }

    private static WorkdayEventRecord Event(long seq, string kind = "task.created", string payload = "{}") =>
        new(seq, _task, kind, "system", payload, _at.AddMinutes(seq));

    private static WorkdayGateDecisionRecord Decision(int n, string gate = "owner", string outcome = "approved") =>
        new(Id(n), _task, gate, outcome, _agent, "approval-" + n, _at.AddMinutes(n));

    [Fact]
    public async Task Workday_writes_every_event_kind_with_canonical_payload()
    {
        var source = new FakeWorkday();
        source.Events.Add(Event(1, "task.created", "{\"b\":1,\"a\":{\"z\":true,\"y\":[2,1]}}"));
        source.Events.Add(Event(3, "task.gate_decided", "{\"outcome\":\"approved\"}"));
        source.Events.Add(Event(7, "task.review_approved"));
        var sink = new RecordingExportSink();

        var result = await new WorkdayExporter(source).ExportAsync(KanbanExportScope.All, sink, CancellationToken.None);

        Assert.Equal(3, result.EventCount);
        var lines = sink.Lines(WorkdayExporter.EventsPath);
        Assert.Equal(3, lines.Length);
        Assert.Equal(
            "{\"seq\":1,\"taskId\":\"" + _task.ToString("D") + "\",\"kind\":\"task.created\",\"actor\":\"system\",\"at\":\"2026-11-18T09:31:00.0000000Z\",\"payload\":{\"a\":{\"y\":[2,1],\"z\":true},\"b\":1}}",
            lines[0]);
        Assert.Equal("task.review_approved", JsonElement.Parse(lines[2]).GetProperty("kind").GetString());
        Assert.Equal(
            ["workday/events.jsonl", "workday/gate-decisions.jsonl", "workday/manifest.json"],
            result.Files);
    }

    [Fact]
    public async Task Workday_writes_gate_decisions_with_provenance_and_a_manifest()
    {
        var source = new FakeWorkday();
        source.Decisions.Add(Decision(1, "council", "rejected"));
        source.Decisions.Add(Decision(2));
        var sink = new RecordingExportSink();

        var result = await new WorkdayExporter(source).ExportAsync(KanbanExportScope.All, sink, CancellationToken.None);

        Assert.Equal(2, result.GateDecisionCount);
        var first = JsonElement.Parse(sink.Lines(WorkdayExporter.GateDecisionsPath)[0]);
        Assert.Equal(Id(1).ToString("D"), first.GetProperty("decisionId").GetString());
        Assert.Equal("council", first.GetProperty("gate").GetString());
        Assert.Equal("rejected", first.GetProperty("outcome").GetString());
        Assert.Equal(_agent.ToString("D"), first.GetProperty("decidedBy").GetString());
        Assert.Equal("approval-1", first.GetProperty("reference").GetString());
        Assert.Equal("2026-11-18T09:31:00.0000000Z", first.GetProperty("decidedAt").GetString());

        var manifest = JsonElement.Parse(sink.Text(WorkdayExporter.ManifestPath));
        Assert.Equal("1.0.0", manifest.GetProperty("schemaVersion").GetString());
        Assert.Equal("proposed", manifest.GetProperty("status").GetString());
        Assert.Equal(2, manifest.GetProperty("gateDecisionCount").GetInt32());
        Assert.Contains(KanbanExporter.TransitionsPath, manifest.GetProperty("companionFiles").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Workday_output_is_deterministic()
    {
        var source = new FakeWorkday();
        source.Events.Add(Event(1, payload: "{\"b\":1,\"a\":2}"));
        source.Decisions.Add(Decision(1));
        var first = new RecordingExportSink();
        var second = new RecordingExportSink();

        await new WorkdayExporter(source).ExportAsync(KanbanExportScope.All, first, CancellationToken.None);
        await new WorkdayExporter(source).ExportAsync(KanbanExportScope.All, second, CancellationToken.None);

        foreach (var path in first.Paths)
        {
            Assert.Equal(first.Bytes(path), second.Bytes(path));
        }
    }

    [Fact]
    public async Task Workday_with_no_data_writes_empty_logs()
    {
        var sink = new RecordingExportSink();

        var result = await new WorkdayExporter(new FakeWorkday()).ExportAsync(KanbanExportScope.All, sink, CancellationToken.None);

        Assert.Equal(0, result.EventCount);
        Assert.Empty(sink.Bytes(WorkdayExporter.EventsPath));
        Assert.Empty(sink.Bytes(WorkdayExporter.GateDecisionsPath));
    }

    [Fact]
    public async Task Workday_refuses_events_out_of_sequence_order()
    {
        var source = new FakeWorkday();
        source.Events.Add(Event(5));
        source.Events.Add(Event(2));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WorkdayExporter(source).ExportAsync(KanbanExportScope.All, new RecordingExportSink(), CancellationToken.None));
    }

    [Fact]
    public async Task Workday_refuses_decisions_out_of_id_order_and_unknown_values()
    {
        var unordered = new FakeWorkday();
        unordered.Decisions.Add(Decision(2));
        unordered.Decisions.Add(Decision(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WorkdayExporter(unordered).ExportAsync(KanbanExportScope.All, new RecordingExportSink(), CancellationToken.None));

        var unknown = new FakeWorkday();
        unknown.Decisions.Add(Decision(1, "none"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WorkdayExporter(unknown).ExportAsync(KanbanExportScope.All, new RecordingExportSink(), CancellationToken.None));
    }

    [Fact]
    public async Task Workday_honours_cancellation()
    {
        var source = new FakeWorkday();
        source.Events.Add(Event(1));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WorkdayExporter(source).ExportAsync(KanbanExportScope.All, new RecordingExportSink(), cts.Token));
    }

    // ---- Orrery ----

    private sealed class FakeOrrery : IOrreryExportSource
    {
        public List<OrreryTriggerRecord> Triggers { get; } = [];

        public List<OrreryFireRecord> Fires { get; } = [];

        public IAsyncEnumerable<OrreryTriggerRecord> ReadTriggersAsync(KanbanExportScope scope, CancellationToken ct) =>
            Stream(Triggers.Where(t => scope.ProjectIds is null || scope.ProjectIds.Contains(t.ProjectId)), ct);

        public IAsyncEnumerable<OrreryFireRecord> ReadFiresAsync(KanbanExportScope scope, CancellationToken ct) => Stream(Fires, ct);
    }

    private static OrreryTriggerRecord Trigger(int n, Guid? project = null, string state = "active", string spec = "{\"fireAt\":\"2026-12-01T00:00:00Z\"}") =>
        new(Id(n), project ?? _projectA, _agent, "timer", [Id(900 + n), Id(800 + n)], "Check the build", spec, null, null, "none", state, _at, 2, 0, null, null, 1, 3, _at, _at);

    private static OrreryFireRecord Fire(int n, Guid trigger, string? cause = "{\"why\":\"due\"}", long? gcSeq = null) =>
        new(Id(n), trigger, _at.AddHours(n), "woke", cause, gcSeq, _at.AddHours(n));

    [Fact]
    public async Task Orrery_writes_trigger_rows_with_every_column_and_restore_paused()
    {
        var source = new FakeOrrery();
        source.Triggers.Add(Trigger(1) with
        {
            TaskId = _task,
            AttachGcId = Id(77),
            PingMode = "attach",
            MaxFires = 5,
            ExpiresAt = _at.AddDays(1),
        });
        var sink = new RecordingExportSink();

        var result = await new OrreryExporter(source).ExportAsync(KanbanExportScope.All, sink, CancellationToken.None);

        Assert.Equal(1, result.TriggerCount);
        var row = JsonElement.Parse(sink.Lines(OrreryExporter.TriggersPath)[0]);
        Assert.Equal(Id(1).ToString("D"), row.GetProperty("triggerId").GetString());
        Assert.Equal("timer", row.GetProperty("kind").GetString());
        Assert.Equal("active", row.GetProperty("state").GetString());
        Assert.Equal("paused", row.GetProperty("restoreAs").GetString());
        Assert.Equal(_task.ToString("D"), row.GetProperty("taskId").GetString());
        Assert.Equal(Id(77).ToString("D"), row.GetProperty("attachGcId").GetString());
        Assert.Equal("attach", row.GetProperty("pingMode").GetString());
        Assert.Equal(5, row.GetProperty("maxFires").GetInt64());
        Assert.Equal(2, row.GetProperty("fireCount").GetInt64());
        Assert.Equal(3, row.GetProperty("version").GetInt64());
        Assert.Equal("2026-12-01T00:00:00Z", row.GetProperty("spec").GetProperty("fireAt").GetString());
        // Targets are sorted, whatever order the source gave.
        var targets = row.GetProperty("targetAgentIds").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal([Id(801).ToString("D"), Id(901).ToString("D")], targets);

        var manifest = JsonElement.Parse(sink.Text(OrreryExporter.ManifestPath));
        Assert.Equal("paused", manifest.GetProperty("restoreAs").GetString());
        Assert.Equal(1, manifest.GetProperty("triggerCount").GetInt32());
    }

    [Fact]
    public async Task Orrery_writes_fires_only_for_exported_triggers()
    {
        var source = new FakeOrrery();
        source.Triggers.Add(Trigger(1, _projectA));
        source.Triggers.Add(Trigger(2, _projectB));
        source.Fires.Add(Fire(10, Id(1), gcSeq: 42));
        source.Fires.Add(Fire(11, Id(2)));
        var sink = new RecordingExportSink();

        var result = await new OrreryExporter(source).ExportAsync(new KanbanExportScope([_projectA]), sink, CancellationToken.None);

        Assert.Equal(1, result.TriggerCount);
        Assert.Equal(1, result.FireCount);
        var fire = JsonElement.Parse(sink.Lines(OrreryExporter.FiresPath)[0]);
        Assert.Equal(Id(10).ToString("D"), fire.GetProperty("fireId").GetString());
        Assert.Equal("woke", fire.GetProperty("outcome").GetString());
        Assert.Equal(42, fire.GetProperty("gcSeq").GetInt64());
        Assert.Equal("due", fire.GetProperty("cause").GetProperty("why").GetString());
    }

    [Fact]
    public async Task Orrery_writes_null_cause_and_gc_seq_when_absent()
    {
        var source = new FakeOrrery();
        source.Triggers.Add(Trigger(1));
        source.Fires.Add(Fire(10, Id(1), cause: null));
        var sink = new RecordingExportSink();

        await new OrreryExporter(source).ExportAsync(KanbanExportScope.All, sink, CancellationToken.None);

        var fire = JsonElement.Parse(sink.Lines(OrreryExporter.FiresPath)[0]);
        Assert.Equal(JsonValueKind.Null, fire.GetProperty("cause").ValueKind);
        Assert.Equal(JsonValueKind.Null, fire.GetProperty("gcSeq").ValueKind);
    }

    [Fact]
    public async Task Orrery_spec_is_canonical_and_output_is_deterministic()
    {
        var source = new FakeOrrery();
        source.Triggers.Add(Trigger(1, spec: "{\"every\":\"PT1H\",\"anchor\":\"2026-01-01T00:00:00Z\"}"));
        var first = new RecordingExportSink();
        var second = new RecordingExportSink();

        await new OrreryExporter(source).ExportAsync(KanbanExportScope.All, first, CancellationToken.None);
        await new OrreryExporter(source).ExportAsync(KanbanExportScope.All, second, CancellationToken.None);

        Assert.Contains("\"spec\":{\"anchor\":\"2026-01-01T00:00:00Z\",\"every\":\"PT1H\"}", first.Text(OrreryExporter.TriggersPath));
        foreach (var path in first.Paths)
        {
            Assert.Equal(first.Bytes(path), second.Bytes(path));
        }
    }

    [Fact]
    public async Task Orrery_refuses_unordered_or_unknown_rows()
    {
        var unordered = new FakeOrrery();
        unordered.Triggers.Add(Trigger(2));
        unordered.Triggers.Add(Trigger(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OrreryExporter(unordered).ExportAsync(KanbanExportScope.All, new RecordingExportSink(), CancellationToken.None));

        var unknown = new FakeOrrery();
        unknown.Triggers.Add(Trigger(1, state: "running"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OrreryExporter(unknown).ExportAsync(KanbanExportScope.All, new RecordingExportSink(), CancellationToken.None));

        var fires = new FakeOrrery();
        fires.Triggers.Add(Trigger(1));
        fires.Fires.Add(Fire(11, Id(1)));
        fires.Fires.Add(Fire(10, Id(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OrreryExporter(fires).ExportAsync(KanbanExportScope.All, new RecordingExportSink(), CancellationToken.None));
    }
}
