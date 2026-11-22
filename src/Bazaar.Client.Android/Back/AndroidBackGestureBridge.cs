using Android.App;
using Android.Window;
using Bazaar.Client.Gestures;

namespace Bazaar.Client.Android.Back;

/// <summary>
/// Feeds the system back gesture into a <see cref="BackGestureRelay"/> (doc 93, "Predictive back"). It registers an
/// <see cref="IOnBackAnimationCallback"/> with the activity's <c>OnBackInvokedDispatcher</c>, so the system reports start, progress (0 to 1,
/// with the swipe edge), commit and cancel, and the shell's <c>PredictiveBackHost</c> draws the transition from the relay.
/// </summary>
/// <remarks>
/// UNVERIFIED, NOT BUILT IN CI. This file was written without the .NET Android workload or the Android SDK, so it has never been compiled
/// against the bindings. The names come from the Android platform API (<c>android.window.OnBackAnimationCallback</c>, <c>BackEvent</c>,
/// API 34) and the usual Xamarin binding rules; the first device build (scripts/build-android.sh) is the check. Fix member names there if a
/// binding differs (for example whether the interface's default methods must all be implemented), and keep the change inside this file.
/// Avalonia 12's own back handling (<c>TopLevel.BackRequested</c>) is not used: it carries no progress. The app's minimum is API 35, so no
/// version guard is needed. The manifest flag <c>enableOnBackInvokedCallback</c> defaults to true for apps that target API 36 or newer
/// (this head targets 37), so no attribute is set; if the target is ever lowered, set it on the application.
///
/// While the callback is registered the system gives it every back, so register only while something can be popped
/// (<see cref="Enabled"/> follows <c>PhoneShell.CanGoBack</c>); at the root back must go to the system and leave the app.
/// </remarks>
public sealed class AndroidBackGestureBridge : Java.Lang.Object, IOnBackAnimationCallback
{
    private readonly Activity _activity;
    private readonly BackGestureRelay _relay;
    private BackGestureEdge _edge = BackGestureEdge.Left;
    private double _progress;
    private bool _registered;

    public AndroidBackGestureBridge(Activity activity, BackGestureRelay relay)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(relay);
        _activity = activity;
        _relay = relay;
    }

    /// <summary>True while the callback is registered. Set it from <c>PhoneShell.CanGoBackChanged</c>. Call on the UI thread.</summary>
    public bool Enabled
    {
        get => _registered;
        set
        {
            if (value == _registered)
            {
                return;
            }

            var dispatcher = _activity.OnBackInvokedDispatcher;
            if (value)
            {
                dispatcher.RegisterOnBackInvokedCallback(IOnBackInvokedDispatcher.PriorityDefault, this);
            }
            else
            {
                dispatcher.UnregisterOnBackInvokedCallback(this);
            }

            _registered = value;
        }
    }

    public void OnBackStarted(BackEvent backEvent)
    {
        _edge = Edge(backEvent);
        _progress = backEvent.Progress;
        _relay.Start(_progress, _edge);
    }

    public void OnBackProgressed(BackEvent backEvent)
    {
        _edge = Edge(backEvent);
        _progress = backEvent.Progress;
        _relay.Progress(_progress, _edge);
    }

    // A back with no gesture (button or three-button navigation) arrives here with no Started before it: the controller treats that as an
    // instant pop, because it was never active.
    public void OnBackInvoked()
    {
        _relay.Commit(_progress, _edge);
        _progress = 0;
    }

    public void OnBackCancelled()
    {
        _relay.Cancel(_progress, _edge);
        _progress = 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Enabled = false;
        }

        base.Dispose(disposing);
    }

    private static BackGestureEdge Edge(BackEvent backEvent) =>
        backEvent.SwipeEdge == BackEvent.EdgeRight ? BackGestureEdge.Right : BackGestureEdge.Left;
}
