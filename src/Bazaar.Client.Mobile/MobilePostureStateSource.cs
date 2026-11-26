using Bazaar.Client.Posture;

namespace Bazaar.Client.Mobile;

/// <summary>
/// Presents the Android head's WindowManager reading (<see cref="IPostureSource"/>, a three-value <see cref="PostureKind"/>) as the
/// posture module's <see cref="Bazaar.Client.Posture.IPostureSource"/>, so the shell's <c>PosturePanel</c> can follow it. Pure managed
/// code with no Android types, so it is tested here; the WindowManager source it wraps is in the Android head.
/// </summary>
/// <remarks>
/// Isolated stub, with a known gap. <see cref="PostureKind"/> carries no hinge geometry, orientation or fold angle, so a half-open
/// reading becomes <see cref="DevicePosture.HalfOpen"/> with no hinge: the layout selector then picks the book layout (a vertical split
/// at the configured ratio) and the unfold transition plays on the posture change instead of following the angle. Closing the gap
/// means carrying <c>IFoldingFeature.Orientation</c> and <c>Bounds</c> through the Android source, which waits on spike S13 (the
/// bound WindowManager surface is unverified, see <c>WindowManagerPostureSource</c>).
/// </remarks>
public sealed class MobilePostureStateSource : Bazaar.Client.Posture.IPostureSource, IDisposable
{
    private readonly IPostureSource _source;
    private readonly IDisposable _subscription;

    public MobilePostureStateSource(IPostureSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _subscription = source.Changes.Subscribe(new Relay(this));
    }

    /// <inheritdoc />
    public PostureState Current => Map(_source.Current);

    /// <inheritdoc />
    public event EventHandler<PostureState>? Changed;

    /// <summary>Folded and unfolded are flat (the window width then decides single or dual pane); half open has no known hinge.</summary>
    public static PostureState Map(PostureKind kind) => kind == PostureKind.HalfOpen
        ? new PostureState(DevicePosture.HalfOpen, null, HingeInset.None)
        : PostureState.Flat;

    public void Dispose() => _subscription.Dispose();

    private sealed class Relay(MobilePostureStateSource owner) : IObserver<PostureKind>
    {
        public void OnNext(PostureKind value) => owner.Changed?.Invoke(owner, Map(value));

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
