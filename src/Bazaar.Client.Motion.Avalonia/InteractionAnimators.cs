using Avalonia;
using Avalonia.Controls;

namespace Bazaar.Client.Motion.Avalonia;

/// <summary>
/// "Touch ripple on any pressable" (doc 93). Liquid: <paramref name="ring"/> (an ellipse in a canvas-like host, aligned top left)
/// grows from the finger. Subtle and reduced: <paramref name="tint"/> (a full-size overlay) pulses. Either control may be null.
/// The ring's size is set once when a ripple starts (layout); during the ripple only transform and opacity change.
/// </summary>
public sealed class RippleAnimator : InteractionAnimator
{
    private readonly Control? _ring;
    private readonly Control? _tint;
    private double _radius;
    private Point _origin;

    public RippleAnimator(Control? ring, Control? tint, MotionSettings settings, IFrameDriver driver)
        : base(settings, driver)
    {
        _ring = ring;
        _tint = tint;
    }

    /// <param name="origin">Where the finger or pointer went down, in the host's coordinates.</param>
    /// <param name="radius">Radius the ring grows to, normally the distance to the farthest corner.</param>
    public void Play(Point origin, double radius)
    {
        _origin = origin;
        _radius = Math.Max(radius, 1);
        if (_ring is not null)
        {
            _ring.Width = _ring.Height = _radius * 2;
        }

        Start(new RipplePlan(Settings.Effective, Settings.Presets));
    }

    protected override void Render(InteractionFrame frame)
    {
        if (_ring is not null)
        {
            MotionTransforms.Apply(_ring, null, frame.ToMotionFrame() with { Tx = _origin.X - _radius, Ty = _origin.Y - _radius });
        }

        _tint?.Opacity = frame.Tint;
    }
}

/// <summary>
/// "Switch toggle" (doc 93). Moves <paramref name="knob"/> across <paramref name="travel"/> device-independent pixels; at the
/// liquid level it stretches while it travels and wobbles once, and <paramref name="trackTint"/> follows the knob so it tints as
/// the knob passes. Flipping in mid-flight keeps the knob's position and velocity.
/// </summary>
public sealed class ToggleAnimator : InteractionAnimator
{
    private readonly Control _knob;
    private readonly Control? _trackTint;
    private readonly double _travel;

    public ToggleAnimator(Control knob, Control? trackTint, double travel, MotionSettings settings, IFrameDriver driver)
        : base(settings, driver)
    {
        _knob = knob;
        _trackTint = trackTint;
        _travel = travel;
    }

    public bool IsChecked { get; private set; }

    /// <summary>The knob position now, 0 (off) to 1 (on).</summary>
    public double Position => Channel.Value;

    /// <summary>Sets the state without animating (first layout, or state pushed from elsewhere).</summary>
    public void Set(bool on)
    {
        Interrupt();
        IsChecked = on;
        SetChannelAtRest(on ? 1 : 0);
        Render(InteractionFrame.Rest with { Tint = on ? 1 : 0, Progress = on ? 1 : 0 });
    }

    /// <summary>Flips to <paramref name="on"/> with the level's animation; a no-op when already heading there.</summary>
    public void Toggle(bool on)
    {
        if (on == IsChecked)
        {
            return;
        }

        IsChecked = on;
        var (position, velocity) = Channel;
        Start(new TogglePlan(Settings.Effective, position, velocity, on, Settings.Presets, Settings.Tokens));
    }

    protected override void Render(InteractionFrame frame)
    {
        MotionTransforms.Apply(_knob, null, frame.ToMotionFrame() with { Tx = frame.Progress * _travel, Opacity = 1 });
        _trackTint?.Opacity = frame.Tint;
    }
}

/// <summary>
/// "Button press" (doc 93). <see cref="Press"/> squashes the button at the liquid level (or darkens <paramref name="darken"/> at
/// the other levels) and <see cref="Release"/> springs it back. Both start from the current value and velocity.
/// </summary>
public sealed class PressAnimator : InteractionAnimator
{
    private readonly Control _target;
    private readonly Control? _darken;

    public PressAnimator(Control target, Control? darken, MotionSettings settings, IFrameDriver driver)
        : base(settings, driver)
    {
        _target = target;
        _darken = darken;
    }

    public void Press() => Go(true);

    public void Release() => Go(false);

    private void Go(bool down)
    {
        var (value, velocity) = Channel;
        Start(new PressPlan(Settings.Effective, value, velocity, down, Settings.Presets, Settings.Tokens));
    }

    protected override void Render(InteractionFrame frame)
    {
        MotionTransforms.Apply(_target, null, frame.ToMotionFrame());
        _darken?.Opacity = frame.Tint;
    }
}

/// <summary>
/// "Send button" (doc 93). <see cref="Arm"/> fills the button like a drop (liquid) or fades it to the accent (other levels) when
/// the composer has text; <see cref="Launch"/> plays the launch when the message goes. <paramref name="fill"/> is an accent
/// overlay inside the button; its scale follows the fill at the liquid level and its opacity the tint.
/// </summary>
public sealed class SendButtonAnimator : InteractionAnimator
{
    private readonly Control _button;
    private readonly Control? _fill;
    private bool _launching;

    public SendButtonAnimator(Control button, Control? fill, MotionSettings settings, IFrameDriver driver)
        : base(settings, driver)
    {
        _button = button;
        _fill = fill;
    }

    /// <summary>The fill now, 0 (empty) to 1 (full).</summary>
    public double Fill => Channel.Value;

    public void Arm(bool armed)
    {
        if (_launching)
        {
            // A launch cut short must not leave the button mid-flight.
            _launching = false;
            MotionTransforms.Apply(_button, null, MotionFrame.Rest);
        }

        var (value, velocity) = Channel;
        Start(new SendFillPlan(Settings.Effective, value, velocity, armed, Settings.Presets));
    }

    public void Launch()
    {
        var restore = Channel;
        Start(new SendLaunchPlan(Settings.Effective, Settings.Presets, Settings.Tokens));
        if (IsRunning)
        {
            _launching = true;
        }
        else
        {
            RestoreFill(restore.Value);
        }
    }

    protected override void Render(InteractionFrame frame)
    {
        if (_launching)
        {
            MotionTransforms.Apply(_button, null, frame.ToMotionFrame());
            if (IsRunning)
            {
                _fill?.Opacity = Math.Max(frame.Tint, Math.Clamp(Channel.Value, 0, 1));
                return;
            }

            _launching = false;
            RestoreFill(Channel.Value);
            return;
        }

        if (_fill is not null)
        {
            var scale = Settings.Effective == MotionLevel.Liquid ? Math.Max(frame.Progress, 0) : 1;
            MotionTransforms.Apply(_fill, null, MotionFrame.Rest with { Scale = scale, Opacity = frame.Tint });
        }
    }

    private void RestoreFill(double value) => _fill?.Opacity = Math.Clamp(value, 0, 1);
}

/// <summary>"Reaction chip added" (doc 93): the chip merges out of the message edge into place.</summary>
public sealed class ReactionPopAnimator : InteractionAnimator
{
    private readonly Control _chip;

    public ReactionPopAnimator(Control chip, MotionSettings settings, IFrameDriver driver)
        : base(settings, driver) => _chip = chip;

    /// <param name="originDx">Where the chip comes from, horizontally, relative to its place.</param>
    /// <param name="originDy">Where the chip comes from, vertically.</param>
    public void Play(double originDx, double originDy) =>
        Start(new ReactionPopPlan(Settings.Effective, originDx, originDy, Settings.Presets));

    protected override void Render(InteractionFrame frame) => MotionTransforms.Apply(_chip, null, frame.ToMotionFrame());
}

/// <summary>"Unread badge and counts" (doc 93): the badge pops in, and bumps when its count changes.</summary>
public sealed class BadgeBumpAnimator : InteractionAnimator
{
    private readonly Control _badge;

    public BadgeBumpAnimator(Control badge, MotionSettings settings, IFrameDriver driver)
        : base(settings, driver) => _badge = badge;

    public void Appear() => Start(new BadgeBumpPlan(Settings.Effective, BadgeChange.Appear, Settings.Presets));

    public void Bump() => Start(new BadgeBumpPlan(Settings.Effective, BadgeChange.CountChange, Settings.Presets));

    protected override void Render(InteractionFrame frame) => MotionTransforms.Apply(_badge, null, frame.ToMotionFrame());
}
