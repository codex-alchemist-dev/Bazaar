namespace Bazaar.Client.Materials;

/// <summary>Where the shell reports what the window system actually did with a backdrop request.</summary>
public interface IMaterialFeedback
{
    /// <summary>The platform refused or ignored the backdrop hint (the window ended up opaque); the policy then draws the opaque fallback.</summary>
    void ReportBackdropFailed();
}

/// <summary>
/// The shared half of every head's <see cref="IMaterialEnvironmentSource"/>. The head supplies a probe for what the platform can do
/// and the conditions that apply (OS build, reduce-transparency, power state); this class caches the probe, because the policy
/// is asked on every layout and an OS read is not free, and layers on what no probe knows: the person's setting, the frame-budget
/// monitor and the backdrop feedback.
/// </summary>
public sealed class PolledMaterialEnvironmentSource : IMaterialEnvironmentSource, IMaterialFeedback
{
    /// <summary>How long a probe result is reused.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromSeconds(2);

    private readonly Func<MaterialEnvironment> _probe;
    private readonly TimeProvider _time;
    private readonly TimeSpan _maxAge;
    private readonly object _gate = new();

    private MaterialEnvironment? _cached;
    private long _cachedAt;
    private MaterialLevel _level = MaterialSettings.Default;
    private bool _frameBudgetMissed;
    private bool _backdropFailed;

    public PolledMaterialEnvironmentSource(Func<MaterialEnvironment> probe, TimeProvider? time = null, TimeSpan? maxAge = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _probe = probe;
        _time = time ?? TimeProvider.System;
        _maxAge = maxAge ?? DefaultMaxAge;
    }

    /// <summary>The person's <c>client.materials</c> setting.</summary>
    public MaterialLevel Level
    {
        get { lock (_gate) { return _level; } }
        set { lock (_gate) { _level = value; } }
    }

    /// <summary>Set by the frame-budget monitor when blur cost more than the frame allows.</summary>
    public bool FrameBudgetMissed
    {
        get { lock (_gate) { return _frameBudgetMissed; } }
        set { lock (_gate) { _frameBudgetMissed = value; } }
    }

    /// <summary>True once the platform has refused the backdrop; it stays true for the session.</summary>
    public bool BackdropRequestFailed
    {
        get { lock (_gate) { return _backdropFailed; } }
    }

    /// <inheritdoc />
    public void ReportBackdropFailed()
    {
        lock (_gate)
        {
            _backdropFailed = true;
        }
    }

    /// <summary>Drops the cached probe so the next read asks the OS again (a head calls this when the window is activated).</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _cached = null;
        }
    }

    /// <inheritdoc />
    public MaterialEnvironment Current
    {
        get
        {
            lock (_gate)
            {
                long now = _time.GetTimestamp();
                if (_cached is null || _time.GetElapsedTime(_cachedAt, now) >= _maxAge)
                {
                    _cached = _probe();
                    _cachedAt = now;
                }

                return _cached with
                {
                    Level = _level,
                    FrameBudgetMissed = _frameBudgetMissed,
                    BackdropRequestFailed = _backdropFailed,
                };
            }
        }
    }
}
