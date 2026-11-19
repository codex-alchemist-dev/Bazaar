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
    public void The_circuit_opens_at_the_threshold_and_closes_after_a_quiet_window()
    {
        var time = new ManualTime();
        var sink = new Sink();
        var tracker = new ParserCrashTracker(2, TimeSpan.FromMinutes(10), sink, time);

        tracker.RecordFailure("a", "first");
        Assert.False(tracker.IsOpen("a"));
        tracker.RecordFailure("a", "second");
        Assert.True(tracker.IsOpen("a"));
        Assert.Equal(ParserFailureKind.CrashLoop, Assert.Throws<ParserSandboxException>(() => tracker.EnsureClosed("a")).Kind);
        Assert.Single(sink.Signals);

        time.Advance(TimeSpan.FromMinutes(11));
        Assert.False(tracker.IsOpen("a"));
        tracker.EnsureClosed("a");
    }

    [Fact]
    public void Failures_outside_the_window_do_not_add_up()
    {
        var time = new ManualTime();
        var tracker = new ParserCrashTracker(2, TimeSpan.FromMinutes(10), null, time);

        tracker.RecordFailure("a", "old");
        time.Advance(TimeSpan.FromMinutes(11));
        tracker.RecordFailure("a", "new");

        Assert.False(tracker.IsOpen("a"));
    }

    [Fact]
    public void A_clean_job_resets_the_count_and_importers_are_independent()
    {
        var tracker = new ParserCrashTracker(2, TimeSpan.FromMinutes(10), null, new ManualTime());

        tracker.RecordFailure("a", "x");
        tracker.RecordSuccess("a");
        tracker.RecordFailure("a", "y");
        tracker.RecordFailure("b", "z");

        Assert.False(tracker.IsOpen("a"));
        Assert.False(tracker.IsOpen("b"));
    }

    [Fact]
    public void Reset_closes_an_open_circuit()
    {
        var tracker = new ParserCrashTracker(1, TimeSpan.FromMinutes(10), null, new ManualTime());
        tracker.RecordFailure("a", "x");
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
    public void The_request_carries_every_import_limit()
    {
        var limits = new ImportLimits(1000, 50, 7, 9, 11, 13);

        var back = ParserRequest.For(ParserOps.Parse, "x", "n", 5, limits).ToLimits();

        Assert.Equal(limits, back);
    }
}
