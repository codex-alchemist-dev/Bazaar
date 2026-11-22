namespace Bazaar.Client.Mobile;

/// <summary>The moments that give a haptic tick (doc 93: commit points, each radial sector, send, approve).</summary>
public enum HapticKind
{
    /// <summary>The finger entered another radial sector.</summary>
    RadialSector,

    /// <summary>A swipe passed its commit point.</summary>
    SwipeCommit,

    /// <summary>A row swipe armed its action.</summary>
    RowCommit,
}

/// <summary>Plays a haptic tick. The Android head implements it with the vibrator; tests use a recorder.</summary>
public interface IHaptics
{
    void Tick(HapticKind kind);
}

/// <summary>
/// Applies the person's <c>android.haptics</c> setting (doc 93, default on) in front of a real <see cref="IHaptics"/>, so a
/// view asks for a tick and never checks the setting itself.
/// </summary>
public sealed class HapticsGate(IHaptics inner, Func<bool> enabled) : IHaptics
{
    private readonly IHaptics _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly Func<bool> _enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));

    public void Tick(HapticKind kind)
    {
        if (_enabled())
        {
            _inner.Tick(kind);
        }
    }
}
