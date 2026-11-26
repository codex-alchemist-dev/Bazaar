using Avalonia.Controls;

namespace Bazaar.Client.Motion.Avalonia;

/// <summary>
/// Plays the droplet bubble (doc 93 catalogue rows "New message bubble" and "Own message sent") on one bubble control. Only
/// transform and opacity change. A frame is requested only while the animation runs, and it can be interrupted at any time:
/// the bubble then eases to rest from its current values. If the motion settings change mid-flight the animation is interrupted too;
/// the next <see cref="Play"/> uses the new level.
/// The merge (goo) effect is optional and behind <see cref="IGooMergeEffect"/>; the default draws nothing and the squash and stretch
/// of the plan is the fallback that is always applied.
/// </summary>
public sealed class DropletBubbleAnimator
{
    private readonly Control _bubble;
    private readonly Control? _content;
    private readonly MotionSettings _settings;
    private readonly IFrameDriver _driver;
    private readonly IGooMergeEffect _goo;

    private DropletPlayer? _player;
    private DropletPlan? _plan;
    private TimeSpan? _lastTimestamp;
    private int _generation;

    /// <param name="bubble">The bubble that moves and fades.</param>
    /// <param name="content">The text inside it; fades in after the shape settles at the liquid level. Null if the bubble has none.</param>
    public DropletBubbleAnimator(Control bubble, Control? content, MotionSettings settings, IFrameDriver driver, IGooMergeEffect? goo = null)
    {
        _bubble = bubble;
        _content = content;
        _settings = settings;
        _driver = driver;
        _goo = goo ?? NoGooMergeEffect.Instance;
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>The level of the animation that is running or ran last; null before the first <see cref="Play"/>.</summary>
    public MotionLevel? PlayedLevel => _plan?.Level;

    public bool IsRunning => _player is { IsComplete: false };

    /// <summary>The last frame applied to the bubble.</summary>
    public MotionFrame LastFrame { get; private set; } = MotionFrame.Rest;

    /// <summary>Raised when the bubble is at rest again, after a finish or an interrupt.</summary>
    public event EventHandler? Completed;

    /// <summary>Starts an animation; a running one is interrupted first. <paramref name="originDx"/> and <paramref name="originDy"/> say where the drop leaves from, relative to the bubble's final place.</summary>
    public void Play(DropletKind kind, double originDx, double originDy)
    {
        if (IsRunning)
        {
            Interrupt();
        }

        _plan = new DropletPlan(_settings.Effective, kind, originDx, originDy, _settings.Tokens);
        _player = new DropletPlayer(_plan);
        _lastTimestamp = null;
        _generation++;
        Apply(_player.Current);
        RequestNext(_generation);
    }

    /// <summary>Stops the scripted animation and eases the bubble to rest (at once at the reduced level).</summary>
    public void Interrupt()
    {
        if (_player is null || _player.IsComplete)
        {
            return;
        }

        _player.Interrupt();
        if (_player.IsComplete)
        {
            Finish();
        }
    }

    /// <summary>Moves the animation forward by hand; frames from the driver call this. Public so tests and custom drivers need no clock.</summary>
    public void Advance(TimeSpan delta)
    {
        if (_player is null || _player.IsComplete)
        {
            return;
        }

        var frame = _player.Advance(delta);
        if (_plan is { Level: MotionLevel.Liquid } && _goo.IsAvailable)
        {
            _goo.TryApply(new GooMergeRequest(frame.Tx, frame.Ty, 0, 0, frame.Scale));
        }

        Apply(frame);
        if (_player.IsComplete)
        {
            Finish();
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => Interrupt();

    private void RequestNext(int generation) => _driver.RequestFrame(ts => Tick(ts, generation));

    private void Tick(TimeSpan timestamp, int generation)
    {
        if (generation != _generation || _player is null || _player.IsComplete)
        {
            return;
        }

        var delta = _lastTimestamp is { } last ? timestamp - last : TimeSpan.Zero;
        _lastTimestamp = timestamp;
        Advance(delta);

        if (_player is { IsComplete: false })
        {
            RequestNext(generation);
        }
    }

    private void Apply(MotionFrame frame)
    {
        LastFrame = frame;
        MotionTransforms.Apply(_bubble, _content, frame);
    }

    private void Finish()
    {
        Apply(MotionFrame.Rest);
        Completed?.Invoke(this, EventArgs.Empty);
    }
}
