namespace Bazaar.Client.Motion.Avalonia;

/// <summary>
/// The live motion state of one client: the person's <c>client.motion_level</c>, the OS reduced-motion flag, the platform and the tokens.
/// <see cref="Effective"/> is what views use; the OS flag forces reduced (doc 93). Changing anything raises <see cref="Changed"/>
/// so running animations can ease to rest. Reading the OS flag is the head's job (it is not exposed uniformly), so the head sets <see cref="OsReducedMotion"/>.
/// </summary>
public sealed class MotionSettings
{
    private MotionLevel? _setting;
    private bool _osReducedMotion;
    private MotionTokens _tokens = MotionTokens.Default;

    public MotionSettings(MotionPlatform platform) => Platform = platform;

    public MotionPlatform Platform { get; }

    /// <summary>The person's choice, or null for the platform default.</summary>
    public MotionLevel? Setting
    {
        get => _setting;
        set => Set(ref _setting, value);
    }

    /// <summary>True when the OS asks for reduced motion; forces <see cref="MotionLevel.Reduced"/>.</summary>
    public bool OsReducedMotion
    {
        get => _osReducedMotion;
        set => Set(ref _osReducedMotion, value);
    }

    /// <summary>Durations and limits; the safety floors are applied on assignment.</summary>
    public MotionTokens Tokens
    {
        get => _tokens;
        set => Set(ref _tokens, value.Clamped());
    }

    /// <summary>The level views use now.</summary>
    public MotionLevel Effective => MotionLevelResolver.Resolve(_setting, _osReducedMotion, Platform);

    /// <summary>Raised after the effective inputs change.</summary>
    public event EventHandler? Changed;

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
