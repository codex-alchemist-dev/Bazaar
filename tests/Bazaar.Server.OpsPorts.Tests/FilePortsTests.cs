using Bazaar.Client.Tui.Dashboard;
using Bazaar.Server.OpsPorts;

namespace Bazaar.Server.OpsPorts.Tests;

public sealed class FilePortsTests : IDisposable
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bazaar-ops-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string File(string name) => Path.Combine(_dir, name);

    [Fact]
    public async Task Log_tail_filters_by_level_and_module_and_keeps_newest_last()
    {
        var writer = new OpsLogFileWriter(File("log.jsonl"));
        writer.Append(new OpsLogLine(_t0, OpsLogLevel.Debug, "kodo", "noise"));
        writer.Append(new OpsLogLine(_t0.AddSeconds(1), OpsLogLevel.Information, "kodo", "one"));
        writer.Append(new OpsLogLine(_t0.AddSeconds(2), OpsLogLevel.Warning, "gateway", "two"));
        writer.Append(new OpsLogLine(_t0.AddSeconds(3), OpsLogLevel.Error, "kodo", "three"));
        var source = new FileOpsLogSource(File("log.jsonl"));

        var all = await source.TailAsync(new OpsLogFilter());
        var kodoOnly = await source.TailAsync(new OpsLogFilter(OpsLogLevel.Information, "KODO"));
        var errors = await source.TailAsync(new OpsLogFilter(OpsLogLevel.Error));
        var lastTwo = await source.TailAsync(new OpsLogFilter(OpsLogLevel.Trace, Limit: 2));

        Assert.Equal(["one", "two", "three"], all.Select(l => l.Message));
        Assert.Equal(["one", "three"], kodoOnly.Select(l => l.Message));
        Assert.Equal(["three"], errors.Select(l => l.Message));
        Assert.Equal(["two", "three"], lastTwo.Select(l => l.Message));
    }

    [Fact]
    public async Task Missing_files_read_as_empty()
    {
        Assert.Empty(await new FileOpsLogSource(File("none.jsonl")).TailAsync(new OpsLogFilter()));
        Assert.Empty(await new FileOpsEventSource(File("none.jsonl")).ReadRecentAsync(10));
        Assert.Empty(await new FileOpsCrashJournal(File("none.jsonl")).ReadRecentAsync(10));
    }

    [Fact]
    public async Task Garbage_and_torn_lines_are_skipped_not_thrown()
    {
        Directory.CreateDirectory(_dir);
        await System.IO.File.WriteAllTextAsync(File("log.jsonl"), "not json\n{\"at\":\"2026-11-22T08:00:00+00:00\",\"level\":\"Error\",\"module\":\"m\",\"message\":\"ok\"}\n{\"at\":");

        var lines = await new FileOpsLogSource(File("log.jsonl")).TailAsync(new OpsLogFilter(OpsLogLevel.Trace));

        Assert.Equal(["ok"], lines.Select(l => l.Message));
    }

    [Fact]
    public async Task A_big_file_is_read_only_from_its_tail()
    {
        var writer = new OpsLogFileWriter(File("log.jsonl"));
        for (var i = 0; i < 400; i++)
        {
            writer.Append(new OpsLogLine(_t0.AddSeconds(i), OpsLogLevel.Information, "m", "line " + i));
        }

        // 4 KiB is the smallest cap the options allow; the cut line at its start is dropped.
        var lines = await new FileOpsLogSource(File("log.jsonl"), 4 * 1024).TailAsync(new OpsLogFilter(Limit: 1000));

        Assert.NotEmpty(lines);
        Assert.True(lines.Count < 400);
        Assert.Equal("line 399", lines[^1].Message);
        Assert.All(lines, l => Assert.StartsWith("line ", l.Message));
    }

    [Fact]
    public async Task Events_come_back_newest_first_with_kind_and_major_kept()
    {
        var writer = new OpsEventFileWriter(File("events.jsonl"));
        writer.Append(new OpsEvent("a", _t0, OpsEventKind.Fault, "disk low"));
        writer.Append(new OpsEvent("b", _t0.AddMinutes(1), OpsEventKind.CrashSurvived, "survived a crash", Major: true));
        writer.Append(new OpsEvent("c", _t0.AddMinutes(2), OpsEventKind.BackupVerified, "backup ok"));

        var events = await new FileOpsEventSource(File("events.jsonl")).ReadRecentAsync(2);

        Assert.Equal(["c", "b"], events.Select(e => e.Id));
        Assert.True(events[1].Major);
        Assert.Equal(OpsEventKind.CrashSurvived, events[1].Kind);
        Assert.True(events[1].FixedItself);
    }

    [Fact]
    public async Task An_unknown_event_kind_reads_as_a_neutral_note()
    {
        Directory.CreateDirectory(_dir);
        await System.IO.File.WriteAllTextAsync(File("events.jsonl"), "{\"id\":\"x\",\"at\":\"2026-11-22T08:00:00+00:00\",\"kind\":\"FromTheFuture\",\"summary\":\"s\",\"major\":false}\n");

        var events = await new FileOpsEventSource(File("events.jsonl")).ReadRecentAsync(5);

        Assert.Equal(OpsEventKind.Info, Assert.Single(events).Kind);
    }

    [Fact]
    public async Task Crash_journal_survives_a_new_instance_and_returns_newest_first()
    {
        await new FileOpsCrashJournal(File("crash.jsonl")).RecordAsync(new OpsCrashNote(_t0, "crashed with A"));
        await new FileOpsCrashJournal(File("crash.jsonl")).RecordAsync(new OpsCrashNote(_t0.AddMinutes(5), "crashed with B"));

        var notes = await new FileOpsCrashJournal(File("crash.jsonl")).ReadRecentAsync(1);

        Assert.Equal("crashed with B", Assert.Single(notes).Reason);
    }

    [Fact]
    public async Task Concurrent_appends_never_interleave_a_line()
    {
        var writer = new OpsLogFileWriter(File("log.jsonl"));
        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => Task.Run(() =>
            writer.Append(new OpsLogLine(_t0.AddSeconds(i), OpsLogLevel.Information, "m", "message " + i)))));

        var lines = await new FileOpsLogSource(File("log.jsonl")).TailAsync(new OpsLogFilter(OpsLogLevel.Trace, Limit: 1000));

        Assert.Equal(50, lines.Count);
    }
}
