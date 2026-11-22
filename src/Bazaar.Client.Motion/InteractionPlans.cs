namespace Bazaar.Client.Motion;

// The first batch of the micro-interaction catalogue (doc 93): ripple, toggle, press, send button, reaction chip, unread badge.
// Every plan is pure (a level, a preset registry and a time in, a frame out), has a liquid, a subtle and a reduced variant, and
// reads its numbers from the preset named in InteractionNames, so a document can retune or turn off any of them.

/// <summary>
/// "Touch ripple on any pressable". Liquid: a soft ring grows from the finger (<see cref="InteractionFrame.Scale"/> is the share
/// of the full ring size, <see cref="InteractionFrame.Opacity"/> its alpha). Subtle: a click tint that rises and falls
/// (<see cref="InteractionFrame.Tint"/>). Reduced: a flat flash of the tint, at most 90 ms. The ring is hidden at rest (opacity 0).
/// </summary>
public sealed class RipplePlan : IInteractionPlan
{
    private readonly MotionLevel _level;
    private readonly InteractionVariant? _variant;

    public RipplePlan(MotionLevel level, InteractionPresets? presets = null)
    {
        _level = level;
        _variant = (presets ?? InteractionPresets.Default).Resolve(InteractionNames.Ripple, level);
        TotalDuration = _variant?.Duration ?? TimeSpan.Zero;
    }

    public TimeSpan TotalDuration { get; }

    /// <summary>The frame with nothing showing.</summary>
    public static InteractionFrame Hidden { get; } = InteractionFrame.Rest with { Opacity = 0 };

    public InteractionFrame Evaluate(TimeSpan elapsed)
    {
        if (_variant is null || elapsed >= TotalDuration)
        {
            return Hidden;
        }

        var u = Math.Clamp(elapsed.TotalSeconds / TotalDuration.TotalSeconds, 0, 1);
        switch (_level)
        {
            case MotionLevel.Liquid:
                var grow = 1 - (1 - u) * (1 - u) * (1 - u);
                return Hidden with { Scale = Math.Max(grow, 0.02), Opacity = _variant.Amount * (1 - u) * (1 - u), Progress = grow };
            case MotionLevel.Subtle:
                return Hidden with { Tint = _variant.Amount * (u < 0.25 ? u / 0.25 : 1 - (u - 0.25) / 0.75) };
            default:
                return Hidden with { Tint = _variant.Amount };
        }
    }
}

/// <summary>
/// "Switch toggle". <see cref="InteractionFrame.Progress"/> is the knob position (0 off, 1 on; it overshoots on the liquid wobble),
/// <see cref="InteractionFrame.Tint"/> is how far the track has tinted (follows the knob, so it tints as the knob passes).
/// Liquid: the knob stretches along its travel with speed and wobbles once. Subtle: a 140 ms slide. Reduced: instant.
/// Built from the knob's current position and velocity, so a flip in mid-flight does not jump.
/// </summary>
public sealed class TogglePlan : IRetargetablePlan
{
    private readonly MotionLevel _level;
    private readonly InteractionVariant? _variant;
    private readonly ValueTransition _position;
    private readonly MotionTokens _tokens;
    private readonly double _target;

    /// <param name="from">The knob position now (0 to 1).</param>
    /// <param name="fromVelocity">The knob velocity now, in positions per second.</param>
    /// <param name="on">The state the toggle goes to.</param>
    public TogglePlan(MotionLevel level, double from, double fromVelocity, bool on, InteractionPresets? presets = null, MotionTokens? tokens = null)
    {
        _level = level;
        _tokens = (tokens ?? MotionTokens.Default).Clamped();
        _variant = (presets ?? InteractionPresets.Default).Resolve(InteractionNames.Toggle, level);
        _target = on ? 1 : 0;
        _position = new ValueTransition(level, _variant, from, fromVelocity, _target);
        TotalDuration = _position.TotalDuration;
    }

    public TimeSpan TotalDuration { get; }

    /// <summary>Knob position and velocity at a time, for building the next plan when the toggle is flipped again.</summary>
    public (double Value, double Velocity) StateAt(TimeSpan elapsed) => _position.At(elapsed);

    public InteractionFrame Evaluate(TimeSpan elapsed)
    {
        var (p, v) = _position.At(elapsed);
        var tint = Math.Clamp(p, 0, 1);
        if (_level != MotionLevel.Liquid || _variant is null || elapsed >= TotalDuration)
        {
            return InteractionFrame.Rest with { Tint = tint, Progress = p };
        }

        // One full knob travel peaks near 3 / duration positions per second; scale that to the stretch limit.
        var speed = Math.Abs(v) * _variant.Duration.TotalSeconds / 3.0 * _variant.Amount;
        var stretch = SquashStretch.FromSpeed(speed, _tokens);
        return InteractionFrame.Rest with { Along = stretch.Along, Across = stretch.Across, Tint = tint, Progress = p };
    }
}

/// <summary>
/// "Button press". The channel is how pressed the button is (0 up, 1 down). Liquid: a vertical squash to the preset scale with
/// volume kept, released on a spring that springs back past rest once. Subtle and reduced: a darken tint
/// (<see cref="InteractionFrame.Tint"/>). Pressing is quick at every level; the preset's duration is the release.
/// </summary>
public sealed class PressPlan : IRetargetablePlan
{
    private readonly MotionLevel _level;
    private readonly InteractionVariant? _variant;
    private readonly ValueTransition _pressed;
    private readonly MotionTokens _tokens;

    /// <param name="from">How pressed the button is now (0 to 1; slightly below 0 while it springs back).</param>
    /// <param name="fromVelocity">The change of that value now, per second.</param>
    /// <param name="down">True for a press, false for a release.</param>
    public PressPlan(MotionLevel level, double from, double fromVelocity, bool down, InteractionPresets? presets = null, MotionTokens? tokens = null)
    {
        _level = level;
        _tokens = (tokens ?? MotionTokens.Default).Clamped();
        _variant = (presets ?? InteractionPresets.Default).Resolve(InteractionNames.Press, level);
        var shape = down && _variant is not null
            ? _variant with { Duration = TimeSpan.FromTicks(_variant.Duration.Ticks * 3 / 10), DampingRatio = 1.0 }
            : _variant;
        _pressed = new ValueTransition(level, shape, from, fromVelocity, down ? 1 : 0);
        TotalDuration = _pressed.TotalDuration;
        Down = down;
    }

    public bool Down { get; }

    public TimeSpan TotalDuration { get; }

    /// <summary>How pressed the button is and how fast that changes, for chaining the next plan.</summary>
    public (double Value, double Velocity) StateAt(TimeSpan elapsed) => _pressed.At(elapsed);

    public InteractionFrame Evaluate(TimeSpan elapsed)
    {
        var (p, _) = _pressed.At(elapsed);
        if (_variant is null)
        {
            return InteractionFrame.Rest;
        }

        if (_level != MotionLevel.Liquid)
        {
            return InteractionFrame.Rest with { Tint = _variant.Amount * Math.Clamp(p, 0, 1), Progress = p };
        }

        var squash = SquashStretch.FromAmount(-(1 - _variant.Amount) * p / (1 - _tokens.MaxSquash), _tokens);
        return InteractionFrame.Rest with { Along = squash.Across, Across = squash.Along, Progress = p };
    }
}

/// <summary>
/// "Send button" fill: when the composer has text the button fills like a drop. <see cref="InteractionFrame.Progress"/> is the fill
/// (0 empty, 1 full; it overshoots once at the liquid level) and <see cref="InteractionFrame.Tint"/> the clamped fill for the
/// "fade to accent" of subtle and reduced.
/// </summary>
public sealed class SendFillPlan : IRetargetablePlan
{
    private readonly ValueTransition _fill;

    public SendFillPlan(MotionLevel level, double from, double fromVelocity, bool armed, InteractionPresets? presets = null)
    {
        _fill = new ValueTransition(level, (presets ?? InteractionPresets.Default).Resolve(InteractionNames.SendFill, level), from, fromVelocity, armed ? 1 : 0);
        TotalDuration = _fill.TotalDuration;
    }

    public TimeSpan TotalDuration { get; }

    public (double Value, double Velocity) StateAt(TimeSpan elapsed) => _fill.At(elapsed);

    public InteractionFrame Evaluate(TimeSpan elapsed)
    {
        var (f, _) = _fill.At(elapsed);
        return InteractionFrame.Rest with { Tint = Math.Clamp(f, 0, 1), Progress = f };
    }
}

/// <summary>
/// "Send button" launch, the moment a message goes. Liquid: the button squashes, the drop leaves upward stretched (the message
/// droplet takes over in the list), and the button re-forms on a spring. Subtle: a tint pulse (fade to accent). Reduced: a flat flash.
/// The final frame is the button at rest.
/// </summary>
public sealed class SendLaunchPlan : IInteractionPlan
{
    private const double _squashEnd = 0.25;
    private const double _flightEnd = 0.65;

    private readonly MotionLevel _level;
    private readonly InteractionVariant? _variant;
    private readonly MotionTokens _tokens;
    private readonly ValueTransition _reform;

    public SendLaunchPlan(MotionLevel level, InteractionPresets? presets = null, MotionTokens? tokens = null)
    {
        _level = level;
        _tokens = (tokens ?? MotionTokens.Default).Clamped();
        _variant = (presets ?? InteractionPresets.Default).Resolve(InteractionNames.SendLaunch, level);
        TotalDuration = _variant?.Duration ?? TimeSpan.Zero;
        var rest = _variant is null ? TimeSpan.Zero : TimeSpan.FromTicks((long)(TotalDuration.Ticks * (1 - _flightEnd)));
        _reform = new ValueTransition(MotionLevel.Liquid, _variant is null ? null : _variant with { Duration = rest, DampingRatio = 0.6 }, 0.6, 0, 1);
    }

    public TimeSpan TotalDuration { get; }

    public InteractionFrame Evaluate(TimeSpan elapsed)
    {
        if (_variant is null || elapsed >= TotalDuration)
        {
            return InteractionFrame.Rest;
        }

        var u = Math.Clamp(elapsed.TotalSeconds / TotalDuration.TotalSeconds, 0, 1);
        if (_level != MotionLevel.Liquid)
        {
            var pulse = _level == MotionLevel.Subtle ? 1 - Math.Abs(2 * u - 1) : 1;
            return InteractionFrame.Rest with { Tint = _variant.Amount * pulse };
        }

        if (u < _squashEnd)
        {
            var s = Smooth(u / _squashEnd);
            var squash = SquashStretch.FromAmount(-s, _tokens);
            return InteractionFrame.Rest with { Along = squash.Across, Across = squash.Along };
        }

        if (u < _flightEnd)
        {
            var s = Smooth((u - _squashEnd) / (_flightEnd - _squashEnd));
            var stretch = SquashStretch.FromSpeed(Math.Sin(Math.PI * s), _tokens);
            return InteractionFrame.Rest with { Ty = -_variant.Amount * s, Along = stretch.Across, Across = stretch.Along, Opacity = 1 - s };
        }

        var rise = TimeSpan.FromSeconds(elapsed.TotalSeconds - TotalDuration.TotalSeconds * _flightEnd);
        var (scale, _) = _reform.At(rise);
        return InteractionFrame.Rest with { Scale = scale, Opacity = Math.Clamp(rise.TotalSeconds / (TotalDuration.TotalSeconds * 0.1), 0, 1) };
    }

    private static double Smooth(double p) => p * p * (3 - 2 * p);
}

/// <summary>
/// "Reaction chip added". Liquid: the chip merges out of the message edge (it starts <c>originDx</c>, <c>originDy</c> away at the
/// preset's small scale) and settles on a spring with one overshoot. Subtle: fade in. Reduced: fade, 90 ms.
/// </summary>
public sealed class ReactionPopPlan : IInteractionPlan
{
    private readonly MotionLevel _level;
    private readonly InteractionVariant? _variant;
    private readonly ValueTransition _settle;
    private readonly double _originDx;
    private readonly double _originDy;

    public ReactionPopPlan(MotionLevel level, double originDx, double originDy, InteractionPresets? presets = null)
    {
        _level = level;
        _originDx = originDx;
        _originDy = originDy;
        _variant = (presets ?? InteractionPresets.Default).Resolve(InteractionNames.ReactionPop, level);
        _settle = new ValueTransition(level, _variant, 0, 0, 1);
        TotalDuration = _settle.TotalDuration;
    }

    public TimeSpan TotalDuration { get; }

    public InteractionFrame Evaluate(TimeSpan elapsed)
    {
        if (_variant is null || elapsed >= TotalDuration)
        {
            return InteractionFrame.Rest;
        }

        var (s, _) = _settle.At(elapsed);
        if (_level != MotionLevel.Liquid)
        {
            return InteractionFrame.Rest with { Opacity = Math.Clamp(s, 0, 1) };
        }

        var fade = Math.Clamp(elapsed.TotalSeconds / (TotalDuration.TotalSeconds * 0.2), 0, 1);
        return InteractionFrame.Rest with
        {
            Tx = _originDx * (1 - s),
            Ty = _originDy * (1 - s),
            Scale = _variant.Amount + (1 - _variant.Amount) * s,
            Opacity = fade,
        };
    }
}

/// <summary>Which badge change a <see cref="BadgeBumpPlan"/> plays.</summary>
public enum BadgeChange
{
    /// <summary>The badge shows up: a drop at the liquid level.</summary>
    Appear,

    /// <summary>The count changes: a bump at the liquid level.</summary>
    CountChange,
}

/// <summary>
/// "Unread badge and counts". Appear, liquid: pops in as a drop on a spring with one overshoot. Count change, liquid: the badge
/// is kicked and swells once. Subtle and reduced: a fade (appear) or a short fade swap (count change). Number rolling is not part of this batch.
/// </summary>
public sealed class BadgeBumpPlan : IInteractionPlan
{
    private readonly MotionLevel _level;
    private readonly BadgeChange _change;
    private readonly InteractionVariant? _variant;
    private readonly ValueTransition _motion;

    public BadgeBumpPlan(MotionLevel level, BadgeChange change, InteractionPresets? presets = null)
    {
        _level = level;
        _change = change;
        _variant = (presets ?? InteractionPresets.Default).Resolve(InteractionNames.BadgeBump, level);
        var kick = change == BadgeChange.CountChange && level == MotionLevel.Liquid;
        _motion = new ValueTransition(level, _variant, kick ? 1 : 0, kick ? _variant?.Amount ?? 0 : 0, 1);
        TotalDuration = _motion.TotalDuration;
    }

    public TimeSpan TotalDuration { get; }

    public InteractionFrame Evaluate(TimeSpan elapsed)
    {
        if (_variant is null || elapsed >= TotalDuration)
        {
            return InteractionFrame.Rest;
        }

        var (s, _) = _motion.At(elapsed);
        if (_level == MotionLevel.Liquid)
        {
            var fade = _change == BadgeChange.Appear ? Math.Clamp(elapsed.TotalSeconds / (TotalDuration.TotalSeconds * 0.2), 0, 1) : 1;
            return InteractionFrame.Rest with { Scale = s, Opacity = fade };
        }

        // A count change swaps through a dip so the new number reads as new.
        return InteractionFrame.Rest with { Opacity = _change == BadgeChange.Appear ? Math.Clamp(s, 0, 1) : 0.4 + 0.6 * Math.Clamp(s, 0, 1) };
    }
}
