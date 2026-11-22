using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Bazaar.Server.Portability.Tests;

public class KanbanExporterTests
{
    private static readonly DateTimeOffset _at = new(2026, 11, 18, 9, 30, 0, TimeSpan.Zero);
    private static readonly Guid _project = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid _otherProject = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid _gc = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid _owner = Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly Guid _taskA = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid _taskB = Guid.Parse("b0000000-0000-0000-0000-000000000001");
    private static readonly Guid _taskC = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid _taskX = Guid.Parse("f0000000-0000-0000-0000-000000000001");

    private static KanbanTaskRecord MakeTask(
        Guid id,
        Guid project,
        string title,
        KanbanColumn column = KanbanColumn.Backlog,
        IReadOnlyList<Guid>? blockers = null,
        IReadOnlyList<KanbanLinkRecord>? links = null) =>
        new(id, project, title, column, null, KanbanGate.None, _gc, null, null, null, null, 1, _at, _at, blockers ?? [], links ?? []);

    private static KanbanEdgeRecord Edge(Guid blocker, Guid blocked) => new(blocker, blocked, "owner:" + _owner.ToString("D"), _at);

    private static KanbanTransitionRecord Transition(long seq, Guid task, KanbanColumn from, KanbanColumn to, string actor, string? reason = null, KanbanGate gate = KanbanGate.None) =>
        new(seq, task, from, to, actor, _at.AddMinutes(seq), reason, gate);

    private static async Task<(KanbanExportResult Result, RecordingExportSink Sink)> RunAsync(FakeKanbanSource source, KanbanExportScope? scope = null)
    {
        var sink = new RecordingExportSink();
        var result = await new KanbanExporter(source).ExportAsync(scope ?? KanbanExportScope.All, sink, CancellationToken.None);
        return (result, sink);
    }

    [Fact]
    public async Task Task_fields_are_written_with_their_stable_names()
    {
        var source = new FakeKanbanSource();
        var task = MakeTask(
            _taskA,
            _project,
            "Ship \"alpha\"",
            KanbanColumn.InProgress,
            links: [new KanbanLinkRecord(KanbanLinkKind.Pr, "codex-alchemist-dev/Bazaar#42"), new KanbanLinkRecord(KanbanLinkKind.Gc, "gc:1")]) with
        {
            OwnerAgentId = _owner,
            Gate = KanbanGate.Council,
            DueAt = _at.AddDays(3),
            ReviewApprovedBy = _owner,
            ReviewApprovedAt = _at.AddHours(1),
            Version = 7,
        };
        source.Tasks.Add(task);

        var (result, sink) = await RunAsync(source);

        Assert.Equal(1, result.TaskCount);
        var line = JsonElement.Parse(sink.Lines(KanbanExporter.TasksPath)[0]);
        Assert.Equal(_taskA.ToString("D"), line.GetProperty("id").GetString());
        Assert.Equal(_project.ToString("D"), line.GetProperty("projectId").GetString());
        Assert.Equal("Ship \"alpha\"", line.GetProperty("title").GetString());
        Assert.Equal("in_progress", line.GetProperty("column").GetString());
        Assert.Equal(_owner.ToString("D"), line.GetProperty("ownerAgentId").GetString());
        Assert.Equal("council", line.GetProperty("gate").GetString());
        Assert.Equal(_gc.ToString("D"), line.GetProperty("originGcId").GetString());
        Assert.Equal(JsonValueKind.Null, line.GetProperty("originMessageId").ValueKind);
        Assert.Equal("2026-11-21T09:30:00.0000000Z", line.GetProperty("dueAt").GetString());
        Assert.Equal(_owner.ToString("D"), line.GetProperty("reviewApprovedBy").GetString());
        Assert.Equal("2026-11-18T10:30:00.0000000Z", line.GetProperty("reviewApprovedAt").GetString());
        Assert.Equal(7, line.GetProperty("version").GetInt64());
        Assert.Equal("2026-11-18T09:30:00.0000000Z", line.GetProperty("createdAt").GetString());
        Assert.False(line.GetProperty("blocked").GetBoolean());
        Assert.Empty(line.GetProperty("blockedBy").EnumerateArray());

        // Links are sorted by kind text, so "gc" comes before "pr".
        var links = line.GetProperty("links").EnumerateArray().ToList();
        Assert.Equal(["gc", "pr"], links.Select(l => l.GetProperty("kind").GetString()!).ToArray());
        Assert.Equal("gc:1", links[0].GetProperty("ref").GetString());
    }

    [Theory]
    [InlineData(KanbanColumn.Backlog, "backlog")]
    [InlineData(KanbanColumn.Ready, "ready")]
    [InlineData(KanbanColumn.InProgress, "in_progress")]
    [InlineData(KanbanColumn.Review, "review")]
    [InlineData(KanbanColumn.Done, "done")]
    public async Task Each_column_uses_its_stable_text(KanbanColumn column, string text)
    {
        var source = new FakeKanbanSource();
        source.Tasks.Add(MakeTask(_taskA, _project, "one", column));

        var (_, sink) = await RunAsync(source);

        var line = JsonElement.Parse(sink.Lines(KanbanExporter.TasksPath)[0]);
        Assert.Equal(text, line.GetProperty("column").GetString());
    }

    [Fact]
    public async Task Blocked_is_a_derived_flag_and_its_reason_lists_the_unfinished_blockers()
    {
        var source = new FakeKanbanSource();
        source.Tasks.Add(MakeTask(_taskA, _project, "blocker", KanbanColumn.Ready));
        source.Tasks.Add(MakeTask(_taskB, _project, "blocked", KanbanColumn.Backlog, blockers: [_taskA]));

        var (_, sink) = await RunAsync(source);

        var lines = sink.Lines(KanbanExporter.TasksPath).Select(l => JsonElement.Parse(l)).ToList();
        Assert.False(lines[0].GetProperty("blocked").GetBoolean());
        Assert.True(lines[1].GetProperty("blocked").GetBoolean());
        Assert.Equal([_taskA.ToString("D")], lines[1].GetProperty("blockedBy").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public async Task Edges_are_written_only_when_both_ends_are_exported()
    {
        var source = new FakeKanbanSource();
        source.Tasks.Add(MakeTask(_taskA, _project, "a"));
        source.Tasks.Add(MakeTask(_taskB, _project, "b"));
        source.Tasks.Add(MakeTask(_taskX, _otherProject, "x"));
        // A -> B has both ends exported. A -> X reaches a task outside the scope, so it must not be written.
        source.Edges.Add(Edge(_taskA, _taskB));
        source.Edges.Add(Edge(_taskA, _taskX));

        var (result, sink) = await RunAsync(source, new KanbanExportScope([_project]));

        Assert.Equal(2, result.TaskCount);
        Assert.Equal(1, result.EdgeCount);
        var edge = JsonElement.Parse(Assert.Single(sink.Lines(KanbanExporter.EdgesPath)));
        Assert.Equal(_taskA.ToString("D"), edge.GetProperty("from").GetString());
        Assert.Equal(_taskB.ToString("D"), edge.GetProperty("to").GetString());
        Assert.Equal("blocks", edge.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Every_edge_is_written_when_the_whole_board_is_exported()
    {
        var source = new FakeKanbanSource();
        source.Tasks.Add(MakeTask(_taskA, _project, "a"));
        source.Tasks.Add(MakeTask(_taskB, _project, "b"));
        source.Tasks.Add(MakeTask(_taskX, _otherProject, "x"));
        source.Edges.Add(Edge(_taskA, _taskB));
        source.Edges.Add(Edge(_taskB, _taskC));
        source.Tasks.Add(MakeTask(_taskC, _otherProject, "c"));

        var (result, sink) = await RunAsync(source);

        Assert.Equal(2, result.EdgeCount);
        Assert.Equal(2, sink.Lines(KanbanExporter.EdgesPath).Length);
    }

    [Fact]
    public async Task Transitions_are_written_for_exported_tasks_only_in_sequence_order()
    {
        var source = new FakeKanbanSource();
        source.Tasks.Add(MakeTask(_taskA, _project, "a"));
        source.Tasks.Add(MakeTask(_taskX, _otherProject, "x"));
        source.Transitions.Add(Transition(1, _taskA, KanbanColumn.Backlog, KanbanColumn.Ready, "owner:" + _owner.ToString("D")));
        source.Transitions.Add(Transition(2, _taskX, KanbanColumn.Ready, KanbanColumn.InProgress, "system"));
        source.Transitions.Add(Transition(3, _taskA, KanbanColumn.Ready, KanbanColumn.InProgress, "agent:" + _owner.ToString("D"), "started the work", KanbanGate.Owner));

        var (result, sink) = await RunAsync(source, new KanbanExportScope([_project]));

        Assert.Equal(2, result.TransitionCount);
        var lines = sink.Lines(KanbanExporter.TransitionsPath).Select(l => JsonElement.Parse(l)).ToList();
        Assert.Equal([1L, 3L], lines.Select(l => l.GetProperty("seq").GetInt64()).ToArray());

        var first = lines[0];
        Assert.Equal(_taskA.ToString("D"), first.GetProperty("taskId").GetString());
        Assert.Equal("backlog", first.GetProperty("from").GetString());
        Assert.Equal("ready", first.GetProperty("to").GetString());
        Assert.Equal("owner:" + _owner.ToString("D"), first.GetProperty("actor").GetString());
        Assert.Equal("2026-11-18T09:31:00.0000000Z", first.GetProperty("at").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("reason").ValueKind);
        Assert.Equal("none", first.GetProperty("gate").GetString());

        var second = lines[1];
        Assert.Equal("ready", second.GetProperty("from").GetString());
        Assert.Equal("in_progress", second.GetProperty("to").GetString());
        Assert.Equal("started the work", second.GetProperty("reason").GetString());
        Assert.Equal("owner", second.GetProperty("gate").GetString());
    }

    [Fact]
    public async Task Boards_list_each_exported_project_once_with_its_task_count()
    {
        var source = new FakeKanbanSource();
        source.Tasks.Add(MakeTask(_taskA, _project, "a"));
        source.Tasks.Add(MakeTask(_taskB, _project, "b"));
        source.Tasks.Add(MakeTask(_taskX, _otherProject, "x"));

        var (_, sink) = await RunAsync(source);

        var boards = JsonElement.Parse(sink.Text(KanbanExporter.BoardsPath));
        Assert.Equal("1.0.0", boards.GetProperty("schemaVersion").GetString());
        Assert.Equal(
            ["backlog", "ready", "in_progress", "review", "done"],
            boards.GetProperty("columns").EnumerateArray().Select(c => c.GetString()!).ToArray());
        Assert.Equal(["blocked"], boards.GetProperty("derived").EnumerateArray().Select(c => c.GetString()!).ToArray());

        var list = boards.GetProperty("boards").EnumerateArray().ToList();
        Assert.Equal(2, list.Count);
        Assert.Equal(_project.ToString("D"), list[0].GetProperty("projectId").GetString());
        Assert.Equal(2, list[0].GetProperty("taskCount").GetInt32());
        Assert.Equal(_otherProject.ToString("D"), list[1].GetProperty("projectId").GetString());
        Assert.Equal(1, list[1].GetProperty("taskCount").GetInt32());
    }

    [Fact]
    public async Task Csv_quotes_fields_and_neutralises_spreadsheet_formulas()
    {
        var source = new FakeKanbanSource();
        source.Tasks.Add(MakeTask(_taskA, _project, "Fix, \"the\" thing"));
        source.Tasks.Add(MakeTask(_taskB, _project, "=HYPERLINK(\"http://example.invalid\")"));
        source.Tasks.Add(MakeTask(_taskC, _project, "two\nlines"));

        var (_, sink) = await RunAsync(source);

        var csv = sink.Text(KanbanExporter.TasksCsvPath);
        Assert.StartsWith("id,project_id,title,column,owner_agent_id,gate,blocked,version,created_at,updated_at\n", csv, StringComparison.Ordinal);
        Assert.Contains("\"Fix, \"\"the\"\" thing\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"'=HYPERLINK(\"\"http://example.invalid\"\")\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"two\nlines\"", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Csv_has_one_row_per_task_after_the_header()
    {
        var source = new FakeKanbanSource();
        source.Tasks.Add(MakeTask(_taskA, _project, "a"));
        source.Tasks.Add(MakeTask(_taskB, _project, "b"));

        var (_, sink) = await RunAsync(source);

        Assert.Equal(3, sink.Lines(KanbanExporter.TasksCsvPath).Length);
    }

    [Fact]
    public async Task Tasks_out_of_id_order_are_refused()
    {
        var source = new FakeKanbanSource { SortRecords = false };
        source.Tasks.Add(MakeTask(_taskB, _project, "b"));
        source.Tasks.Add(MakeTask(_taskA, _project, "a"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(source));
    }

    [Fact]
    public async Task Transitions_out_of_sequence_order_are_refused()
    {
        var source = new FakeKanbanSource { SortRecords = false };
        source.Tasks.Add(MakeTask(_taskA, _project, "a"));
        source.Transitions.Add(Transition(5, _taskA, KanbanColumn.Backlog, KanbanColumn.Ready, "system"));
        source.Transitions.Add(Transition(4, _taskA, KanbanColumn.Ready, KanbanColumn.Backlog, "system"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(source));
    }

    [Fact]
    public async Task Output_bytes_are_identical_for_the_same_data_however_the_source_orders_it()
    {
        var first = SeededSource(reverse: false);
        var second = SeededSource(reverse: true);

        var (_, sinkOne) = await RunAsync(first);
        var (_, sinkTwo) = await RunAsync(second);

        Assert.Equal(sinkOne.Paths, sinkTwo.Paths);
        foreach (var path in sinkOne.Paths)
        {
            Assert.True(sinkOne.Bytes(path).AsSpan().SequenceEqual(sinkTwo.Bytes(path)), "bytes differ for " + path);
        }
    }

    [Fact]
    public async Task Exporting_the_same_source_twice_gives_the_same_bytes()
    {
        var source = SeededSource(reverse: false);

        var (_, sinkOne) = await RunAsync(source);
        var (_, sinkTwo) = await RunAsync(source);

        foreach (var path in sinkOne.Paths)
        {
            Assert.True(sinkOne.Bytes(path).AsSpan().SequenceEqual(sinkTwo.Bytes(path)), "bytes differ for " + path);
        }
    }

    [Fact]
    public async Task Output_uses_lf_line_endings_and_no_byte_order_mark()
    {
        var (_, sink) = await RunAsync(SeededSource(reverse: false));

        foreach (var path in sink.Paths)
        {
            var bytes = sink.Bytes(path);
            Assert.False(bytes.Contains((byte)'\r'), path + " has a carriage return");
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, path + " has a byte order mark");
        }
    }

    [Fact]
    public async Task Empty_source_writes_every_file_with_no_rows()
    {
        var (result, sink) = await RunAsync(new FakeKanbanSource());

        Assert.Equal(0, result.TaskCount);
        Assert.Equal(0, result.EdgeCount);
        Assert.Equal(0, result.TransitionCount);
        Assert.Equal(
            [KanbanExporter.BoardsPath, KanbanExporter.EdgesPath, KanbanExporter.TasksCsvPath, KanbanExporter.TasksPath, KanbanExporter.TransitionsPath],
            sink.Paths);
        Assert.Empty(sink.Bytes(KanbanExporter.TasksPath));
        Assert.Equal("id,project_id,title,column,owner_agent_id,gate,blocked,version,created_at,updated_at\n", sink.Text(KanbanExporter.TasksCsvPath));
    }

    [Fact]
    public async Task Result_lists_the_files_it_wrote()
    {
        var (result, _) = await RunAsync(SeededSource(reverse: false));

        Assert.Equal(
            [KanbanExporter.TasksPath, KanbanExporter.TasksCsvPath, KanbanExporter.EdgesPath, KanbanExporter.TransitionsPath, KanbanExporter.BoardsPath],
            result.Files);
    }

    private static FakeKanbanSource SeededSource(bool reverse)
    {
        var source = new FakeKanbanSource();
        var tasks = new List<KanbanTaskRecord>
        {
            MakeTask(_taskA, _project, "design", KanbanColumn.Done),
            MakeTask(_taskB, _project, "build", KanbanColumn.Ready, blockers: [_taskA], links: [new KanbanLinkRecord(KanbanLinkKind.Commit, "abc123")]),
            MakeTask(_taskX, _otherProject, "other board", KanbanColumn.Review),
        };
        var edges = new List<KanbanEdgeRecord> { Edge(_taskA, _taskB) };
        var transitions = new List<KanbanTransitionRecord>
        {
            Transition(1, _taskA, KanbanColumn.Backlog, KanbanColumn.Ready, "system"),
            Transition(2, _taskA, KanbanColumn.Ready, KanbanColumn.Done, "owner:" + _owner.ToString("D"), "accepted"),
        };
        if (reverse)
        {
            tasks.Reverse();
            edges.Reverse();
            transitions.Reverse();
        }

        source.Tasks.AddRange(tasks);
        source.Edges.AddRange(edges);
        source.Transitions.AddRange(transitions);
        return source;
    }

    /// <summary>An in-memory source. It applies the scope to tasks only, so the exporter's own edge and transition guards are tested.</summary>
    private sealed class FakeKanbanSource : IKanbanExportSource
    {
        public List<KanbanTaskRecord> Tasks { get; } = [];

        public List<KanbanEdgeRecord> Edges { get; } = [];

        public List<KanbanTransitionRecord> Transitions { get; } = [];

        /// <summary>When false, records are yielded in insertion order, which lets a test present an out-of-order source.</summary>
        public bool SortRecords { get; init; } = true;

        public async IAsyncEnumerable<KanbanTaskRecord> ReadTasksAsync(KanbanExportScope scope, [EnumeratorCancellation] CancellationToken ct)
        {
            var rows = Tasks.Where(t => scope.ProjectIds is null || scope.ProjectIds.Contains(t.ProjectId));
            if (SortRecords)
            {
                rows = rows.OrderBy(t => t.Id.ToString("D"), StringComparer.Ordinal);
            }

            foreach (var row in rows.ToList())
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return row;
            }
        }

        public async IAsyncEnumerable<KanbanEdgeRecord> ReadEdgesAsync(KanbanExportScope scope, [EnumeratorCancellation] CancellationToken ct)
        {
            var rows = Edges.AsEnumerable();
            if (SortRecords)
            {
                rows = rows.OrderBy(e => e.BlockerId.ToString("D"), StringComparer.Ordinal).ThenBy(e => e.BlockedId.ToString("D"), StringComparer.Ordinal);
            }

            foreach (var row in rows.ToList())
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return row;
            }
        }

        public async IAsyncEnumerable<KanbanTransitionRecord> ReadTransitionsAsync(KanbanExportScope scope, [EnumeratorCancellation] CancellationToken ct)
        {
            var rows = Transitions.AsEnumerable();
            if (SortRecords)
            {
                rows = rows.OrderBy(t => t.Seq);
            }

            foreach (var row in rows.ToList())
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return row;
            }
        }
    }
}
