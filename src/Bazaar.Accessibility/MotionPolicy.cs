namespace Bazaar.Accessibility;

/// <summary>
/// The reduced-motion rule (doc 49 section 2: motion can be reduced). One place decides how long an animation runs, so
/// a module asks the policy instead of reading the theme flag itself. Reduced motion makes every animation instant and
/// turns off purely decorative ones; essential motion that carries meaning (a progress bar) may still ask for
/// <see cref="Essential"/> and keep a shortened duration.
/// </summary>
public sealed class MotionPolicy
{
    /// <summary>The longest an essential animation may run when motion is reduced.</summary>
    public static readonly TimeSpan EssentialCap = TimeSpan.FromMilliseconds(120);

    private bool _reduced;

    public MotionPolicy(bool reduced = false) => _reduced = reduced;

    /// <summary>Raised after <see cref="Reduced"/> changes.</summary>
    public event EventHandler? Changed;

    public bool Reduced
    {
        get => _reduced;
        set
        {
            if (_reduced == value)
            {
                return;
            }

            _reduced = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The duration for a decorative animation: the requested one, or zero when motion is reduced.</summary>
    public TimeSpan Decorative(TimeSpan requested) => _reduced ? TimeSpan.Zero : NonNegative(requested);

    /// <summary>The duration for an animation that carries meaning: the requested one, capped to <see cref="EssentialCap"/> when motion is reduced.</summary>
    public TimeSpan Essential(TimeSpan requested)
    {
        TimeSpan value = NonNegative(requested);
        return _reduced && value > EssentialCap ? EssentialCap : value;
    }

    /// <summary>Whether a purely decorative effect (a twinkle, a parallax drift, a looping shimmer) may run at all.</summary>
    public bool AllowDecorativeLoops => !_reduced;

    private static TimeSpan NonNegative(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;
}
