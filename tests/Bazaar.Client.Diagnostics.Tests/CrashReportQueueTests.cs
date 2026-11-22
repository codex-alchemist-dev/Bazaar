using System.Text.Json;
using Bazaar.Contracts.Telemetry;

namespace Bazaar.Client.Diagnostics.Tests;

public class CrashReportQueueTests : IDisposable
{
    private static readonly DateTimeOffset _start = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _root = new();
    private readonly TestClock _clock = new(_start);

    public void Dispose()
    {
        _root.Dispose();
        GC.SuppressFinalize(this);
    }

    private CrashReportQueue Queue(CrashQueueLimits? limits = null) =>
        new(_root.Sub("queue"), limits ?? CrashQueueLimits.Default, _clock);

    private static Guid Id(int n) => new($"00000000-0000-7000-8000-{n:D12}");

    [Fact]
    public void Add_then_list_round_trips_the_report()
    {
        var queue = Queue();
        var report = Samples.Report(Id(1), _start);

        queue.Add(report);
        var listed = queue.List();

        var entry = Assert.Single(listed);
        Assert.Equal(Id(1), entry.ReportId);
        Assert.False(entry.Quarantined);
        Assert.Equal(report.Error.Code, entry.Report.Error.Code);
        Assert.Equal(report.Fingerprint, entry.Report.Fingerprint);
        Assert.Equal(report.CreatedAt, entry.Report.CreatedAt);
    }

    [Fact]
    public void Add_leaves_no_temporary_file_behind()
    {
        Queue().Add(Samples.Report(Id(1), _start));

        var names = Directory.GetFiles(_root.Sub("queue")).Select(f => Path.GetFileName(f)!).ToArray();
        Assert.Equal([Id(1).ToString("D") + ".json"], names);
    }

    [Fact]
    public void Count_cap_drops_the_oldest_first()
    {
        var queue = Queue(new CrashQueueLimits(2, long.MaxValue, TimeSpan.FromDays(30)));

        queue.Add(Samples.Report(Id(1), _start));
        queue.Add(Samples.Report(Id(2), _start.AddMinutes(1)));
        queue.Add(Samples.Report(Id(3), _start.AddMinutes(2)));

        Assert.Equal([Id(2), Id(3)], queue.List().Select(e => e.ReportId).ToArray());
    }

    [Fact]
    public void Byte_cap_drops_the_oldest_first()
    {
        var one = JsonSerializer.SerializeToUtf8Bytes(Samples.Report(Id(1), _start), CrashContractsJsonContext.Default.CrashReport).Length;
        var queue = Queue(new CrashQueueLimits(100, (2L * one) + 10, TimeSpan.FromDays(30)));

        queue.Add(Samples.Report(Id(1), _start));
        queue.Add(Samples.Report(Id(2), _start.AddMinutes(1)));
        queue.Add(Samples.Report(Id(3), _start.AddMinutes(2)));

        Assert.Equal([Id(2), Id(3)], queue.List().Select(e => e.ReportId).ToArray());
    }

    [Fact]
    public void Reports_older_than_the_age_limit_are_dropped_when_the_queue_is_written()
    {
        var queue = Queue(new CrashQueueLimits(100, long.MaxValue, TimeSpan.FromDays(1)));
        queue.Add(Samples.Report(Id(1), _start));

        _clock.Advance(TimeSpan.FromDays(2));
        queue.Add(Samples.Report(Id(2), _clock.GetUtcNow()));

        Assert.Equal([Id(2)], queue.List().Select(e => e.ReportId).ToArray());
    }

    [Fact]
    public void Corrupt_queue_files_are_deleted_and_foreign_files_are_left_alone()
    {
        var dir = _root.Sub("queue");
        Directory.CreateDirectory(dir);
        var corrupt = Path.Combine(dir, Id(7).ToString("D") + ".json");
        File.WriteAllText(corrupt, "{ not json");
        var mismatched = Path.Combine(dir, Id(8).ToString("D") + ".json");
        File.WriteAllBytes(mismatched, JsonSerializer.SerializeToUtf8Bytes(Samples.Report(Id(9), _start), CrashContractsJsonContext.Default.CrashReport));
        var foreign = Path.Combine(dir, "notes.json");
        File.WriteAllText(foreign, "hello");
        var readme = Path.Combine(dir, "readme.txt");
        File.WriteAllText(readme, "hello");

        var listed = Queue().List();

        Assert.Empty(listed);
        Assert.False(File.Exists(corrupt));
        Assert.False(File.Exists(mismatched));
        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(readme));
    }

    [Fact]
    public void Ids_never_reach_outside_the_queue_directory()
    {
        var outside = _root.Sub("outside.json");
        File.WriteAllText(outside, "keep");
        var queue = Queue();
        queue.Add(Samples.Report(Id(1), _start));

        Assert.False(queue.Remove(Id(2)));
        Assert.False(queue.Quarantine(Id(2)));
        Assert.True(queue.Remove(Id(1)));

        Assert.True(File.Exists(outside));
        Assert.Equal("keep", File.ReadAllText(outside));
    }

    [Fact]
    public void Quarantined_reports_are_kept_on_disk_but_not_listed()
    {
        var queue = Queue();
        queue.Add(Samples.Report(Id(1), _start));

        Assert.True(queue.Quarantine(Id(1)));

        Assert.Empty(queue.List());
        Assert.True(File.Exists(Path.Combine(_root.Sub("queue"), Id(1).ToString("D") + ".refused")));
    }
}
