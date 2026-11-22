using Android.App;
using AndroidX.Window.Layout;
using Bazaar.Client.Mobile;

namespace Bazaar.Client.Android.Posture;

/// <summary>
/// Android posture source over Jetpack WindowManager (doc 29, WP2). It reads the activity's folding feature, passes the
/// platform values to <see cref="FoldPosture.Map(FoldSnapshot)"/> and emits only real changes. Provisional, pending spike S13.
/// </summary>
/// <remarks>
/// Binding note: Xamarin.AndroidX.Window 1.5.1.3 has no Activity, Executor and Consumer listener on
/// <c>WindowInfoTracker</c>. That listener lives in androidx.window:window-java, which is not bound. The bound reads are
/// <c>IWindowInfoTracker.GetCurrentWindowLayoutInfo(Context)</c> (synchronous) and the Kotlin Flow <c>WindowLayoutInfo(Activity)</c>,
/// so this source reads on demand through <see cref="Refresh"/> instead of subscribing. The head must call it from the
/// activity's configuration-change callback. Whether that delivers every fold change is an open question for spike S13.
/// </remarks>
public sealed class WindowManagerPostureSource : IPostureSource
{
    private readonly IWindowInfoTracker _tracker;
    private readonly List<IObserver<PostureKind>> _observers = [];
    private Activity? _activity;

    /// <summary>Creates the source for one activity. Call on the UI thread.</summary>
    public WindowManagerPostureSource(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        _activity = activity;
        _tracker = WindowInfoTracker.GetOrCreate(activity);
        Current = FoldPosture.Map(ReadSnapshot(_tracker, activity));
    }

    public PostureKind Current { get; private set; }

    public IObservable<PostureKind> Changes => new Subscribable(this);

    /// <summary>
    /// Reads the fold state again and emits the new posture if it changed. Call on the UI thread, for example from the
    /// activity's <c>OnConfigurationChanged</c>. Nothing is emitted when the posture is the same as before.
    /// </summary>
    public void Refresh()
    {
        var activity = _activity ?? throw new ObjectDisposedException(nameof(WindowManagerPostureSource));
        var next = FoldPosture.Map(ReadSnapshot(_tracker, activity));
        if (next == Current)
        {
            return;
        }

        Current = next;
        foreach (var observer in _observers.ToArray())
        {
            observer.OnNext(next);
        }
    }

    public void Dispose()
    {
        if (_activity is null)
        {
            return;
        }

        // No platform listener is registered, so there is nothing to unregister. Observers are completed and the activity is released.
        _activity = null;
        foreach (var observer in _observers.ToArray())
        {
            observer.OnCompleted();
        }

        _observers.Clear();
    }

    /// <summary>Translates the platform layout into the pure snapshot. No folding feature gives <see cref="FoldSnapshot.NoFold"/>.</summary>
    private static FoldSnapshot ReadSnapshot(IWindowInfoTracker tracker, Activity activity)
    {
        var info = tracker.GetCurrentWindowLayoutInfo(activity);
        foreach (var feature in info.DisplayFeatures)
        {
            if (feature is IFoldingFeature folding)
            {
                var halfOpened = Equals(folding.State, FoldingFeatureState.HalfOpened);
                var horizontal = Equals(folding.Orientation, FoldingFeatureOrientation.Horizontal);
                return new FoldSnapshot(
                    HasFoldingFeature: true,
                    State: halfOpened ? FoldState.HalfOpened : FoldState.Flat,
                    HingeIsHorizontal: horizontal,
                    HingeIsSeparating: folding.IsSeparating);
            }
        }

        return FoldSnapshot.NoFold;
    }

    private sealed class Subscribable(WindowManagerPostureSource owner) : IObservable<PostureKind>
    {
        public IDisposable Subscribe(IObserver<PostureKind> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            var unsubscriber = new Unsubscriber(owner._observers, observer);
            if (owner._activity is null)
            {
                // A disposed source has nothing more to report, so the observer completes at once.
                observer.OnCompleted();
                return unsubscriber;
            }

            owner._observers.Add(observer);
            return unsubscriber;
        }
    }

    private sealed class Unsubscriber(List<IObserver<PostureKind>> observers, IObserver<PostureKind> observer) : IDisposable
    {
        public void Dispose() => observers.Remove(observer);
    }
}
