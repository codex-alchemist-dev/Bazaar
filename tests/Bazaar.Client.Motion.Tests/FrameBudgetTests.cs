namespace Bazaar.Client.Motion.Tests;

public class FrameBudgetMonitorTests
{
    private static readonly TimeSpan _frame = TimeSpan.FromMilliseconds(16.67);

    private static FrameBudgetMonitor Monitor() =>
        new(new FrameBudgetOptions(TimeSpan.FromMilliseconds(16.67), WindowSize: 20, MinSamples: 10, RecoverEvaluations: 2, StaleAfter: TimeSpan.FromSeconds(30)));

    private static void Feed(FrameBudgetMonitor monitor, double ms, int count)
    {
        for (int i = 0; i < count; i++)
        {
            monitor.Record(TimeSpan.FromMilliseconds(ms));
        }
    }

    [Fact]
    public void No_verdict_before_enough_samples()
    {
        var monitor = Monitor();

        Feed(monitor, 100, 9);

        Assert.Equal(FrameBudgetDegradation.None, monitor.Degradation);
        Assert.Null(monitor.P95);
    }

    [Fact]
    public void Steady_frames_inside_the_budget_never_degrade()
    {
        var monitor = Monitor();
        int changes = 0;
        monitor.Changed += (_, _) => changes++;

        Feed(monitor, 8.3, 200);
        Feed(monitor, 16.7, 200);

        Assert.Equal(FrameBudgetDegradation.None, monitor.Degradation);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void A_few_slow_frames_among_good_ones_do_not_trip_p95()
    {
        var monitor = Monitor();

        // One slow frame in twenty is exactly the 5% the p95 ignores.
        for (int i = 0; i < 100; i++)
        {
            monitor.Record(i % 20 == 0 ? TimeSpan.FromMilliseconds(80) : _frame);
        }

        Assert.Equal(FrameBudgetDegradation.None, monitor.Degradation);
    }

    [Fact]
    public void Sustained_slow_frames_degrade_lightly_and_raise_changed()
    {
        var monitor = Monitor();
        int changes = 0;
        monitor.Changed += (_, _) => changes++;

        Feed(monitor, 30, 10);

        Assert.Equal(FrameBudgetDegradation.Light, monitor.Degradation);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Very_slow_frames_degrade_heavily()
    {
        var monitor = Monitor();

        Feed(monitor, 70, 10);

        Assert.Equal(FrameBudgetDegradation.Heavy, monitor.Degradation);
    }

    [Fact]
    public void Recovery_needs_consecutive_good_evaluations_one_level_at_a_time()
    {
        var monitor = Monitor();
        Feed(monitor, 70, 10);
        Assert.Equal(FrameBudgetDegradation.Heavy, monitor.Degradation);
        Feed(monitor, 16, 20); // flush the window with good frames

        // One good evaluation is not enough.
        Assert.Equal(FrameBudgetDegradation.Heavy, monitor.Degradation);

        Feed(monitor, 16, 10);
        Assert.Equal(FrameBudgetDegradation.Light, monitor.Degradation);

        Feed(monitor, 16, 20);
        Assert.Equal(FrameBudgetDegradation.None, monitor.Degradation);
    }

    [Fact]
    public void Frames_between_the_recover_and_degrade_thresholds_hold_the_level()
    {
        var monitor = Monitor();
        Feed(monitor, 30, 10);
        Assert.Equal(FrameBudgetDegradation.Light, monitor.Degradation);

        // 20 ms is under 1.5x the budget (no further step down) but over 1.15x (no recovery): the dead band.
        Feed(monitor, 20, 200);

        Assert.Equal(FrameBudgetDegradation.Light, monitor.Degradation);
    }

    [Fact]
    public void An_interrupted_recovery_restarts_the_count()
    {
        var monitor = Monitor();
        Feed(monitor, 30, 10);
        Feed(monitor, 16, 20); // flush, one good evaluation (the next only counts after a flush)
        Feed(monitor, 20, 20); // dead band: restarts the count
        Feed(monitor, 16, 10);

        Assert.Equal(FrameBudgetDegradation.Light, monitor.Degradation);
    }

    [Fact]
    public void A_long_pause_forgets_old_samples_and_steps_up_one_level()
    {
        var monitor = Monitor();
        Feed(monitor, 70, 10);

        monitor.NoteIdle(TimeSpan.FromSeconds(5));
        Assert.Equal(FrameBudgetDegradation.Heavy, monitor.Degradation);

        monitor.NoteIdle(TimeSpan.FromSeconds(31));
        Assert.Equal(FrameBudgetDegradation.Light, monitor.Degradation);
        Assert.Null(monitor.P95);
    }

    [Fact]
    public void Non_positive_frame_times_are_ignored()
    {
        var monitor = Monitor();

        Feed(monitor, 0, 50);

        Assert.Null(monitor.P95);
    }

    [Fact]
    public void Options_out_of_range_throw()
    {
        Assert.Throws<ArgumentException>(() => new FrameBudgetMonitor(new FrameBudgetOptions(TimeSpan.Zero)));
        Assert.Throws<ArgumentException>(() => new FrameBudgetMonitor(new FrameBudgetOptions(_frame, WindowSize: 10, MinSamples: 20)));
    }
}

public class FrameTimeTrackerTests
{
    private sealed class Recorder : IFrameTimeSink
    {
        public List<TimeSpan> Frames { get; } = [];

        public List<TimeSpan> Idles { get; } = [];

        public void FrameTime(TimeSpan frameTime) => Frames.Add(frameTime);

        public void Resumed(TimeSpan idle) => Idles.Add(idle);
    }

    [Fact]
    public void The_first_frame_reports_nothing_and_later_frames_report_the_gap()
    {
        var sink = new Recorder();
        var tracker = new FrameTimeTracker(sink);

        tracker.OnFrame(TimeSpan.FromMilliseconds(1000));
        tracker.OnFrame(TimeSpan.FromMilliseconds(1016));
        tracker.OnFrame(TimeSpan.FromMilliseconds(1033));

        Assert.Equal([TimeSpan.FromMilliseconds(16), TimeSpan.FromMilliseconds(17)], sink.Frames);
        Assert.Empty(sink.Idles);
    }

    [Fact]
    public void A_gap_over_the_limit_is_stillness_not_a_slow_frame()
    {
        var sink = new Recorder();
        var tracker = new FrameTimeTracker(sink);

        tracker.OnFrame(TimeSpan.FromMilliseconds(0));
        tracker.OnFrame(TimeSpan.FromSeconds(10));
        tracker.OnFrame(TimeSpan.FromSeconds(10) + TimeSpan.FromMilliseconds(16));

        Assert.Single(sink.Idles);
        Assert.Equal(TimeSpan.FromSeconds(10), sink.Idles[0]);
        Assert.Equal([TimeSpan.FromMilliseconds(16)], sink.Frames);
    }

    [Fact]
    public void A_dropped_frame_still_counts_as_a_slow_frame()
    {
        var sink = new Recorder();
        var tracker = new FrameTimeTracker(sink);

        tracker.OnFrame(TimeSpan.Zero);
        tracker.OnFrame(TimeSpan.FromMilliseconds(50));

        Assert.Equal([TimeSpan.FromMilliseconds(50)], sink.Frames);
    }

    [Fact]
    public void The_budget_sink_feeds_the_monitor()
    {
        var monitor = new FrameBudgetMonitor(new FrameBudgetOptions(TimeSpan.FromMilliseconds(16.67), WindowSize: 20, MinSamples: 10));
        var tracker = new FrameTimeTracker(new FrameBudgetSink(monitor));

        for (int i = 0; i <= 12; i++)
        {
            tracker.OnFrame(TimeSpan.FromMilliseconds(i * 40));
        }

        Assert.Equal(FrameBudgetDegradation.Light, monitor.Degradation);
    }
}

public class PolledOsReducedMotionSourceTests
{
    [Fact]
    public void Reads_the_probe_at_construction()
    {
        var source = new PolledOsReducedMotionSource(() => true);

        Assert.True(source.Current);
    }

    [Fact]
    public void Refresh_raises_changed_only_when_the_answer_changes()
    {
        bool os = false;
        var source = new PolledOsReducedMotionSource(() => os);
        int raised = 0;
        source.Changed += (_, _) => raised++;

        source.Refresh();
        Assert.Equal(0, raised);

        os = true;
        source.Refresh();
        source.Refresh();

        Assert.True(source.Current);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void A_throwing_probe_keeps_the_last_value()
    {
        bool fail = false;
        var source = new PolledOsReducedMotionSource(() => fail ? throw new InvalidOperationException() : true);
        fail = true;

        source.Refresh();

        Assert.True(source.Current);
    }
}
