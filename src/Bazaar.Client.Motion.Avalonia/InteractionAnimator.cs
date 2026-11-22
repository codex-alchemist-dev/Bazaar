namespace Bazaar.Client.Motion.Avalonia;

/// <summary>
/// Runs one <see cref="IInteractionPlan"/> at a time on a control. A frame is requested only while a plan runs, so nothing costs
/// anything at rest. Starting a plan replaces the running one; for a retargetable plan (toggle, press, send fill) the next plan
/// starts from the current value and velocity, so a flip in mid-flight does not jump. A change of motion settings ends the running
/// plan at its final frame, and the next one uses the new level. Only transform and opacity are ever written.
/// </summary>
public abstract class InteractionAnimator
{
    private readonly IFrameDriver _driver;
    private IInteractionPlan? _plan;
    private TimeSpan _elapsed;
    private TimeSpan? _lastTimestamp;
    private int _generation;
    private (double Value, double Velocity) _channel;

    protected InteractionAnimator(MotionSettings settings, IFrameDriver driver)
    {
        Settings = settings;
        _driver = driver;
        settings.Changed += (_, _) => Interrupt();
    }

    protected MotionSettings Settings { get; }

    public bool IsRunning { get; private set; }

    /// <summary>The last frame applied.</summary>
    public InteractionFrame LastFrame { get; private set; } = InteractionFrame.Rest;

    /// <summary>Raised when a plan has ended, after its final frame is applied.</summary>
    public event EventHandler? Completed;

    /// <summary>The retargetable number now (running or at rest) and its velocity.</summary>
    protected (double Value, double Velocity) Channel =>
        IsRunning && _plan is IRetargetablePlan r ? r.StateAt(_elapsed) : _channel;

    /// <summary>Sets the number at rest, for a control that starts in a known state.</summary>
    protected void SetChannelAtRest(double value) => _channel = (value, 0);

    /// <summary>Moves a running plan forward by hand; frames from the driver call this. Public so tests need no clock.</summary>
    public void Advance(TimeSpan delta)
    {
        if (!IsRunning || _plan is null)
        {
            return;
        }

        _elapsed += delta;
        if (_elapsed >= _plan.TotalDuration)
        {
            Finish();
            return;
        }

        Render(LastFrame = _plan.Evaluate(_elapsed));
    }

    /// <summary>Ends the running plan at its final frame.</summary>
    public void Interrupt()
    {
        if (IsRunning)
        {
            Finish();
        }
    }

    protected void Start(IInteractionPlan plan)
    {
        _channel = Channel;
        _plan = plan;
        _elapsed = TimeSpan.Zero;
        _lastTimestamp = null;
        _generation++;
        IsRunning = true;
        if (plan.TotalDuration <= TimeSpan.Zero)
        {
            Finish();
            return;
        }

        Render(LastFrame = plan.Evaluate(TimeSpan.Zero));
        RequestNext(_generation);
    }

    /// <summary>Writes the frame to the controls.</summary>
    protected abstract void Render(InteractionFrame frame);

    private void RequestNext(int generation) => _driver.RequestFrame(ts => Tick(ts, generation));

    private void Tick(TimeSpan timestamp, int generation)
    {
        if (generation != _generation || !IsRunning)
        {
            return;
        }

        var delta = _lastTimestamp is { } last ? timestamp - last : TimeSpan.Zero;
        _lastTimestamp = timestamp;
        Advance(delta);
        if (IsRunning)
        {
            RequestNext(generation);
        }
    }

    private void Finish()
    {
        var plan = _plan!;
        var final = plan.Evaluate(plan.TotalDuration);
        if (plan is IRetargetablePlan r)
        {
            _channel = r.StateAt(plan.TotalDuration);
        }

        IsRunning = false;
        _generation++;
        Render(LastFrame = final);
        Completed?.Invoke(this, EventArgs.Empty);
    }
}
