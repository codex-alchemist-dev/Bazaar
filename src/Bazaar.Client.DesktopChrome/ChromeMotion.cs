using Bazaar.Client.Motion;
using Bazaar.Client.Motion.Avalonia;

namespace Bazaar.Client.DesktopChrome;

/// <summary>
/// The desktop motion rules for the chrome (doc 93, Motion language and the micro-interaction catalogue), resolved from the theme's
/// <c>motion.*</c> tokens and the live <see cref="MotionSettings"/>. Subtle (the desktop default) scales the phone durations by
/// <c>motion.scale.desktop</c> and never overshoots; liquid on desktop keeps the full durations; reduced is a fade of
/// <c>motion.duration.reduced</c>, at most 90 ms, with no travel.
/// </summary>
public sealed class ChromeMotion
{
    private readonly ChromeTheme _theme;

    public ChromeMotion(ChromeTheme theme, MotionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(settings);
        _theme = theme;
        Settings = settings;
    }

    public MotionSettings Settings { get; }

    public MotionLevel Level => Settings.Effective;

    /// <summary>False at the reduced level: nothing travels or resizes, it only fades.</summary>
    public bool Travels => Level != MotionLevel.Reduced;

    /// <summary>How far an entering surface drops or slides, in dip: the motion token for the subtle slide, zero when reduced.</summary>
    public double Travel => Travels ? Settings.Tokens.SubtleSlide : 0;

    /// <summary>Tab-class changes (sidebar, title swap). The theme value is already the desktop one; reduced shortens it to the fade.</summary>
    public TimeSpan Tab => Resolve("motion.duration.tab", scaled: false);

    /// <summary>The sheet duration: the phone value scaled for desktop, or the reduced fade.</summary>
    public TimeSpan Sheet => Resolve("motion.duration.sheet", scaled: true);

    /// <summary>The curve: a critically damped spring (no overshoot) at subtle and liquid, linear at reduced.</summary>
    public bool UsesSpring => Travels;

    private TimeSpan Resolve(string token, bool scaled)
    {
        if (!Travels)
        {
            TimeSpan reduced = _theme.Duration("motion.duration.reduced");
            return reduced > MotionTokens.ReducedFadeCeiling ? MotionTokens.ReducedFadeCeiling : reduced;
        }

        TimeSpan duration = _theme.Duration(token);
        if (!scaled || Level == MotionLevel.Liquid)
        {
            return duration;
        }

        double scale = Math.Clamp(_theme.Number("motion.scale.desktop"), 0.0, 1.0);
        return new TimeSpan((long)(duration.Ticks * scale));
    }
}
