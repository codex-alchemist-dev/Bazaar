namespace Bazaar.Client.Motion;

/// <summary>Receives frame times of running animations (the frame-budget monitor, or a test).</summary>
public interface IFrameTimeSink
{
    /// <summary>The time between two consecutive frames of a running animation.</summary>
    void FrameTime(TimeSpan frameTime);

    /// <summary>Animation resumed after <paramref name="idle"/> of stillness.</summary>
    void Resumed(TimeSpan idle);
}

/// <summary>
/// Turns the timestamps of animation frame callbacks into frame times. An animation requests a frame only while it runs (doc 93), so a
/// long gap between two callbacks is stillness, not a slow frame: it is reported as <see cref="IFrameTimeSink.Resumed"/> and never as a
/// frame time. Nothing here asks for frames, so measuring costs nothing at rest. One tracker per frame driver (timestamps of different
/// drivers are not comparable).
/// </summary>
public sealed class FrameTimeTracker(IFrameTimeSink sink, TimeSpan? maxContinuousGap = null)
{
    /// <summary>Gaps longer than this are stillness. Four frames at 60 Hz, well above one dropped frame.</summary>
    public static readonly TimeSpan DefaultMaxContinuousGap = TimeSpan.FromMilliseconds(100);

    private readonly TimeSpan _maxGap = maxContinuousGap ?? DefaultMaxContinuousGap;
    private TimeSpan? _last;

    /// <summary>Call with the timestamp of each frame callback.</summary>
    public void OnFrame(TimeSpan timestamp)
    {
        if (_last is { } last)
        {
            TimeSpan gap = timestamp - last;
            if (gap > TimeSpan.Zero)
            {
                if (gap <= _maxGap)
                {
                    sink.FrameTime(gap);
                }
                else
                {
                    sink.Resumed(gap);
                }
            }
        }

        _last = timestamp;
    }
}

/// <summary>Feeds a <see cref="FrameBudgetMonitor"/> from frame times.</summary>
public sealed class FrameBudgetSink(FrameBudgetMonitor monitor) : IFrameTimeSink
{
    /// <inheritdoc />
    public void FrameTime(TimeSpan frameTime) => monitor.Record(frameTime);

    /// <inheritdoc />
    public void Resumed(TimeSpan idle) => monitor.NoteIdle(idle);
}
