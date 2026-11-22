namespace Bazaar.Constellation.Rendering.Core;

/// <summary>What the pacer is doing. Idle renders nothing; the rest differ only in target rate.</summary>
public enum PacerMode
{
    Idle,
    InteractingSmall,
    InteractingBig,
    Settling,
}

public enum FrameDueKind
{
    /// <summary>Nothing to draw (standstill): sleep until input arrives. No timer needed.</summary>
    Skip,

    /// <summary>Draw immediately.</summary>
    RenderNow,

    /// <summary>Draw after the delay; never later than the next allowed frame time.</summary>
    Wait,
}

/// <summary>Answer to "should a frame be drawn now?". <see cref="Delay"/> is meaningful only for <see cref="FrameDueKind.Wait"/>.</summary>
public readonly record struct FrameDue(FrameDueKind Kind, TimeSpan Delay)
{
    public static FrameDue Skip { get; } = new(FrameDueKind.Skip, TimeSpan.Zero);

    public static FrameDue RenderNow { get; } = new(FrameDueKind.RenderNow, TimeSpan.Zero);

    public static FrameDue Wait(TimeSpan delay) => new(FrameDueKind.Wait, delay);
}

/// <summary>
/// Tunables for <see cref="FramePacer"/>. Owner requirement: nothing renders at a standstill, input wakes instantly, big movement
/// runs at the display's full refresh rate and small movement at a lower one (120 Hz display: idle 0, small about 90, big 120).
/// </summary>
public sealed record FramePacerOptions
{
    /// <summary>Camera movement (pixels, or zoom converted with <see cref="FramePacer.ZoomRatioToPixels"/>) at or above this in one input counts as big. Default 150.</summary>
    public float BigDeltaPixels { get; init; } = 150f;

    /// <summary>Small-movement rate as a fraction of the display rate. Default 0.75 (90 of 120 Hz).</summary>
    public float SmallFraction { get; init; } = 0.75f;

    /// <summary>How long after the last input the camera still counts as interacting. Default 100 ms.</summary>
    public TimeSpan InputHold { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>How long a big input keeps the big rate, so one slow event inside a fling does not flap the rate. Default 200 ms.</summary>
    public TimeSpan BigHold { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Tail after interaction ends so inertia and fades finish before Idle. Default 400 ms.</summary>
    public TimeSpan SettleTime { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Zoom pixels per unit of natural-log zoom ratio, for <see cref="FramePacer.ZoomRatioToPixels"/>. Default 1000 (a 2x zoom is about 693 px).</summary>
    public float ZoomPixelsPerLogUnit { get; init; } = 1000f;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(BigDeltaPixels);
        if (!(SmallFraction > 0f && SmallFraction <= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(SmallFraction), SmallFraction, "Must be in (0, 1].");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(InputHold, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(BigHold, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(SettleTime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ZoomPixelsPerLogUnit);
    }
}

/// <summary>
/// Decides when to draw. Pure and deterministic: all time comes from the injected clock or an explicit timestamp, and there are no
/// sleeps or timers here; the host sleeps for the returned delay (or until input on Skip). Frames are paced from the start of the
/// last frame actually drawn, never from a fixed grid, so a late frame (variable refresh, a slow GPU) is followed by one normal
/// interval rather than a burst of catch-up frames, and the pacer never asks for a faster cadence than the target.
/// </summary>
public sealed class FramePacer
{
    private readonly Func<TimeSpan> _clock;
    private readonly FramePacerOptions _options;
    private double _displayHz;
    private TimeSpan? _lastInput;
    private TimeSpan? _lastBigInput;
    private TimeSpan? _lastFrameStart;
    private bool _wakePending;
    private bool _animating;

    public FramePacer(double displayRefreshHz, Func<TimeSpan> clock, FramePacerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _options = options ?? new FramePacerOptions();
        _options.Validate();
        _clock = clock;
        DisplayRefreshHz = displayRefreshHz;
    }

    /// <summary>The display's maximum refresh rate in Hz; the full-rate target. Set again when the window moves to another display.</summary>
    public double DisplayRefreshHz
    {
        get => _displayHz;
        set
        {
            if (!(value > 0) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Refresh rate must be a positive finite number.");
            }

            _displayHz = value;
        }
    }

    /// <summary>Current mode as of the injected clock. The quality side reads this to pick a lower target and more detail on small movement.</summary>
    public PacerMode Mode => ModeAt(_clock());

    /// <summary>Target rate for the current mode: 0 when Idle, the display rate for big movement, the small fraction otherwise.</summary>
    public double TargetFps => TargetFpsFor(Mode);

    /// <summary>
    /// Frame-time target in ms for the current mode, for building or retargeting a <see cref="FrameBudgetController"/>.
    /// Idle reports the full-rate frame time (the budget a woken frame must meet).
    /// </summary>
    public double TargetFrameMs => FrameBudgetController.TargetForRefreshRate(Mode is PacerMode.Idle ? _displayHz : TargetFpsFor(Mode));

    /// <summary>True while the pacer wants more frames: any non-Idle mode, or an animation reported through <see cref="SetAnimating"/>.</summary>
    public bool NeedsAnotherFrame => _animating || Mode != PacerMode.Idle;

    /// <summary>Converts a zoom ratio (2 = twice as close, 0.5 = twice as far) to pixels for <see cref="OnInput(float, TimeSpan)"/>.</summary>
    public float ZoomRatioToPixels(float ratio) =>
        ratio > 0f ? MathF.Abs(MathF.Log(ratio)) * _options.ZoomPixelsPerLogUnit : 0f;

    /// <summary>Records camera input at the injected clock's time.</summary>
    public void OnInput(float cameraDeltaPixels) => OnInput(cameraDeltaPixels, _clock());

    /// <summary>
    /// Records camera input. <paramref name="cameraDeltaPixels"/> is the movement magnitude in pixels (use
    /// <see cref="ZoomRatioToPixels"/> for zoom); zero means a button or key was pressed without movement yet, which still wakes.
    /// </summary>
    public void OnInput(float cameraDeltaPixels, TimeSpan timestamp)
    {
        if (ModeAt(timestamp) == PacerMode.Idle)
        {
            _wakePending = true;
        }

        _lastInput = timestamp;
        if (MathF.Abs(cameraDeltaPixels) >= _options.BigDeltaPixels)
        {
            _lastBigInput = timestamp;
        }
    }

    /// <summary>Makes the next frame due immediately (any input, resize, data change) and starts the settle tail.</summary>
    public void Wake()
    {
        TimeSpan now = _clock();
        if (ModeAt(now) == PacerMode.Idle)
        {
            _wakePending = true;
        }

        _lastInput = now;
    }

    /// <summary>Reports whether an animation or fade is running outside camera input; keeps frames coming until it ends.</summary>
    public void SetAnimating(bool animating)
    {
        if (animating && !_animating)
        {
            _wakePending = true;
        }

        _animating = animating;
    }

    /// <summary>Whether to draw at <paramref name="now"/>. Skip means standstill (0 fps).</summary>
    public FrameDue NextFrameDue(TimeSpan now)
    {
        PacerMode mode = ModeAt(now);
        if (mode == PacerMode.Idle && !_animating && !_wakePending)
        {
            return FrameDue.Skip;
        }

        if (_wakePending || _lastFrameStart is not { } last)
        {
            return FrameDue.RenderNow;
        }

        double fps = mode == PacerMode.Idle ? TargetFpsFor(PacerMode.Settling) : TargetFpsFor(mode);
        TimeSpan due = last + TimeSpan.FromSeconds(1.0 / fps);
        return now >= due ? FrameDue.RenderNow : FrameDue.Wait(due - now);
    }

    /// <summary>The host calls this when it starts drawing a frame. Pacing restarts from here, so lateness is not made up.</summary>
    public void OnFrameStarted(TimeSpan now)
    {
        _lastFrameStart = now;
        _wakePending = false;
    }

    private PacerMode ModeAt(TimeSpan now)
    {
        if (_lastInput is not { } input || now < input)
        {
            return PacerMode.Idle;
        }

        TimeSpan since = now - input;
        if (since < _options.InputHold || since == TimeSpan.Zero)
        {
            bool big = _lastBigInput is { } b && now - b < _options.BigHold;
            return big ? PacerMode.InteractingBig : PacerMode.InteractingSmall;
        }

        return since < _options.InputHold + _options.SettleTime ? PacerMode.Settling : PacerMode.Idle;
    }

    private double TargetFpsFor(PacerMode mode) => mode switch
    {
        PacerMode.Idle => 0,
        PacerMode.InteractingBig => _displayHz,
        _ => _displayHz * _options.SmallFraction,
    };
}
