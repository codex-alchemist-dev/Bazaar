namespace Bazaar.Client.Motion;

/// <summary>Where a head reports the operating system's reduced-motion preference (doc 93: it forces the reduced level).</summary>
public interface IOsReducedMotionSource
{
    /// <summary>True when the OS asks for less motion.</summary>
    bool Current { get; }

    /// <summary>Raised when <see cref="Current"/> changes, from whichever thread called the refresh.</summary>
    event EventHandler? Changed;
}

/// <summary>
/// An <see cref="IOsReducedMotionSource"/> over a head-supplied probe. The probe is read at construction and on <see cref="Refresh"/>
/// (a head calls it when the window is activated or the app resumes, where the OS offers no change event). A probe that throws leaves
/// the last value, so a failed OS read never turns motion off or on by accident.
/// </summary>
public sealed class PolledOsReducedMotionSource : IOsReducedMotionSource
{
    private readonly Func<bool> _probe;
    private readonly Lock _gate = new();
    private bool _current;

    public PolledOsReducedMotionSource(Func<bool> probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _probe = probe;
        _current = SafeProbe(false);
    }

    /// <inheritdoc />
    public bool Current
    {
        get { lock (_gate) { return _current; } }
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>Reads the OS again and raises <see cref="Changed"/> when the answer differs.</summary>
    public void Refresh()
    {
        bool changed;
        lock (_gate)
        {
            bool now = SafeProbe(_current);
            changed = now != _current;
            _current = now;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool SafeProbe(bool fallback)
    {
        try
        {
            return _probe();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return fallback;
        }
    }
}
