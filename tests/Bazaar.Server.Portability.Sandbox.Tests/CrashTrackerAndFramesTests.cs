using Bazaar.Server.Portability;

namespace Bazaar.Server.Portability.Sandbox.Tests;

public class CrashTrackerAndFramesTests
{
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Sink : IParserSignalSink
    {
        public List<ParserSignal> Signals { get; } = [];

        public void Raise(ParserSignal signal) => Signals.Add(signal);
    }

    [Fact]
    public void The_circuit_opens_only_when_failures_come_from_two_distinct_sources()
    {
        var time = new ManualTime();
        var sink = new Sink();
        var tracker = new ParserCrashTracker(2, TimeSpan.FromMinutes(10), sink, time, sourceFailureCap: 10);

        tracker.RecordFailure("a", "s1", "first");
        tracker.RecordFailure("a", "s1", "second");
        Assert.False(tracker.IsOpen("a"));
        Assert.Empty(sink.Signals);

        tracker.RecordFailure("a", "s2", "third");
        Assert.True(tracker.IsOpen("a"));
        Assert.Single(sink.Signals);
        Assert.Equal(ParserFailureKind.CrashLoop, Assert.Throws<ParserSandboxException>(() => tracker.EnsureClosed("a", "s9")).Kind);

        time.Advance(TimeSpan.FromMinutes(11));
        Assert.False(tracker.IsOpen("a"));
        tracker.EnsureClosed("a", "s9");
    }

    [Fact]
    public void A_source_that_hits_its_failure_cap_is_refused_and_others_are_not()
    {
        var tracker = new ParserCrashTracker(10, TimeSpan.FromMinutes(10), null, new ManualTime(), sourceFailureCap: 2);

        tracker.RecordFailure("a", "s1", "x", abnormal: false);
        tracker.EnsureClosed("a", "s1");
        tracker.RecordFailure("a", "s1", "y", abnormal: false);

        Assert.True(tracker.IsSourceBlocked("a", "s1"));
        Assert.False(tracker.IsOpen("a"));
        Assert.Equal(ParserFailureKind.CrashLoop, Assert.Throws<ParserSandboxException>(() => tracker.EnsureClosed("a", "s1")).Kind);
        tracker.EnsureClosed("a", "s2");
    }

    [Fact]
    public void A_failure_that_is_not_abnormal_never_counts_toward_the_circuit()
    {
        var tracker = new ParserCrashTracker(1, TimeSpan.FromMinutes(10), null, new ManualTime(), sourceFailureCap: 10);

        tracker.RecordFailure("a", "s1", "limit", abnormal: false);
        tracker.RecordFailure("a", "s2", "limit", abnormal: false);

        Assert.False(tracker.IsOpen("a"));
    }

    [Fact]
    public void Failures_outside_the_window_do_not_add_up()
    {
        var time = new ManualTime();
        var tracker = new ParserCrashTracker(2, TimeSpan.FromMinutes(10), null, time);

        tracker.RecordFailure("a", "s1", "old");
        time.Advance(TimeSpan.FromMinutes(11));
        tracker.RecordFailure("a", "s2", "new");

        Assert.False(tracker.IsOpen("a"));
    }

    [Fact]
    public void A_clean_job_clears_that_source_and_importers_are_independent()
    {
        var tracker = new ParserCrashTracker(2, TimeSpan.FromMinutes(10), null, new ManualTime(), sourceFailureCap: 10);

        tracker.RecordFailure("a", "s1", "x");
        tracker.RecordSuccess("a", "s1");
        tracker.RecordFailure("a", "s2", "y");
        tracker.RecordFailure("b", "s3", "z");

        Assert.False(tracker.IsOpen("a"));
        Assert.False(tracker.IsOpen("b"));
    }

    [Fact]
    public void Reset_closes_an_open_circuit()
    {
        var tracker = new ParserCrashTracker(1, TimeSpan.FromMinutes(10), null, new ManualTime(), sourceFailureCap: 10);
        tracker.RecordFailure("a", "s1", "x");
        tracker.RecordFailure("a", "s2", "x");
        Assert.True(tracker.IsOpen("a"));

        tracker.Reset("a");

        Assert.False(tracker.IsOpen("a"));
    }

    [Fact]
    public async Task Frames_round_trip_a_record_and_refuse_an_oversize_frame()
    {
        using var stream = new MemoryStream();
        var record = new SourceRecord("k", "document", "t", "body \"quoted\" é", new Dictionary<string, string> { ["a"] = "b" });
        ParserFrames.Write(stream, new ParserMessage { Type = ParserMessageTypes.Record, Record = record });
        stream.Position = 0;

        var read = await ParserFrames.ReadMessageAsync(stream, 1024 * 1024, CancellationToken.None);

        Assert.Equal(record.Body, read!.Record!.Body);
        Assert.Equal("b", read.Record.Attributes["a"]);

        stream.Position = 0;
        await Assert.ThrowsAsync<FrameTooLargeException>(() => ParserFrames.ReadMessageAsync(stream, 4, CancellationToken.None));
    }

    [Fact]
    public async Task A_truncated_frame_is_an_error_and_an_empty_stream_is_a_clean_end()
    {
        using var empty = new MemoryStream();
        Assert.Null(await ParserFrames.ReadMessageAsync(empty, 1024, CancellationToken.None));

        using var truncated = new MemoryStream([10, 0, 0, 0, 1, 2]);
        await Assert.ThrowsAsync<InvalidDataException>(() => ParserFrames.ReadMessageAsync(truncated, 1024, CancellationToken.None));
    }

    [Fact]
    public void A_record_is_sized_by_every_string_field_including_attributes()
    {
        var record = new SourceRecord("k", "kind", "title", "body", new Dictionary<string, string> { ["key"] = "value" });

        Assert.Equal(1 + 4 + 5 + 4 + 3 + 5, ParserFrames.RecordBytes(record));
    }

    [Fact]
    public void The_request_carries_every_import_limit()
    {
        var limits = new ImportLimits(1000, 50, 7, 9, 11, 13);

        var back = ParserRequest.For(ParserOps.Parse, "x", "n", 5, limits).ToLimits();

        Assert.Equal(limits, back);
    }

    [Fact]
    public async Task A_frame_header_with_no_body_does_not_allocate_the_announced_length()
    {
        // Announces 100 MiB, then ends. The reader must fail without ever holding that much.
        var header = BitConverter.GetBytes(100 * 1024 * 1024);
        using var stream = new MemoryStream(header);
        var before = GC.GetAllocatedBytesForCurrentThread();

        await Assert.ThrowsAsync<InvalidDataException>(() => ParserFrames.ReadFrameAsync(stream, 200 * 1024 * 1024, CancellationToken.None));

        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 8 * 1024 * 1024);
    }

    [Fact]
    public void Importers_with_no_recent_failures_are_pruned_from_the_tracker()
    {
        var time = new ManualTime();
        var tracker = new ParserCrashTracker(3, TimeSpan.FromMinutes(10), null, time);
        tracker.RecordFailure("a", "s", "x");
        tracker.RecordFailure("b", "s", "x");
        Assert.Equal(2, tracker.TrackedImporterCount);

        time.Advance(TimeSpan.FromMinutes(11));
        tracker.RecordFailure("c", "s", "x");

        Assert.Equal(1, tracker.TrackedImporterCount);
    }
}
