namespace Bazaar.Client.Core.Mobile;

/// <summary>
/// Raw fold data as the platform reports it (Jetpack WindowManager's FoldingFeature on Android). Pure data:
/// the head fills it in, <see cref="FoldPosture.Map(FoldSnapshot)"/> turns it into a <see cref="PostureKind"/> (doc 29, WP2).
/// </summary>
public sealed record FoldSnapshot(bool HasFoldingFeature, FoldState State, bool HingeIsHorizontal, bool HingeIsSeparating)
{
    /// <summary>A device with no fold: the phone layout, as <see cref="FoldPosture.Map(bool, FoldState, bool, bool)"/> defines it.</summary>
    public static FoldSnapshot NoFold { get; } = new(false, FoldState.Flat, false, false);
}

/// <summary>
/// Source of the current posture. Heads implement it over the platform (Android: WindowManager). Views depend only on this
/// interface, so tests use <see cref="FakePostureSource"/>. The source reports changes; it never computes layout (doc 29, C27).
/// </summary>
public interface IPostureSource : IDisposable
{
    /// <summary>The posture now.</summary>
    PostureKind Current { get; }

    /// <summary>Emits when the posture changes. Repeated identical postures are not emitted.</summary>
    IObservable<PostureKind> Changes { get; }
}
