using Android.App;
using Android.Content.PM;
using Android.Content.Res;
using Android.OS;
using Avalonia.Android;
using Bazaar.Client.Android.Back;
using Bazaar.Client.Android.Posture;
using Bazaar.Client.Gestures;

namespace Bazaar.Client.Android;

// Folding, unfolding and rotating must not recreate the activity: the phone switches layout variants locally (doc 29).
// Provisional, pending spike S13. UiMode is kept from the earlier setting; the design list does not name it.
[Activity(
    Label = "Bazaar",
    Theme = "@style/Theme.Bazaar",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation
        | ConfigChanges.ScreenSize
        | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize
        | ConfigChanges.Density
        | ConfigChanges.KeyboardHidden
        | ConfigChanges.LayoutDirection
        | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    private WindowManagerPostureSource? _posture;
    private AndroidBackGestureBridge? _backBridge;

    /// <summary>The running activity, or null before OnCreate and after OnDestroy. The app has one activity.</summary>
    public static MainActivity? Current { get; private set; }

    /// <summary>The fold posture source for this activity, or null before OnCreate and after OnDestroy.</summary>
    public WindowManagerPostureSource? Posture => _posture;

    /// <summary>The system back gesture for this activity (doc 93, "Predictive back"), or null before OnCreate and after OnDestroy.</summary>
    public BackGestureRelay? BackGestures { get; private set; }

    /// <summary>Turns the system back callback on or off. On only while the shell has a screen to pop, so back at the root leaves the app.</summary>
    public void SetBackCallbackEnabled(bool enabled)
    {
        if (_backBridge is not null)
        {
            _backBridge.Enabled = enabled;
        }
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // The back source exists before base.OnCreate: that is where Avalonia starts the app and builds the shell, which reads it.
        // The bridge registers with the system only later, when the shell has a screen to pop.
        Current = this;
        BackGestures = new BackGestureRelay();
        _backBridge = new AndroidBackGestureBridge(this, BackGestures);
        base.OnCreate(savedInstanceState);
        _posture = new WindowManagerPostureSource(this);
    }

    // The source has no listener binding yet (see WindowManagerPostureSource), so the activity pulls the layout here.
    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        _posture?.Refresh();
    }

    protected override void OnResume()
    {
        base.OnResume();
        // The person may have switched "Remove animations" in system settings while the app was in the background.
        App.ReducedMotion?.Refresh();
    }

    protected override void OnDestroy()
    {
        _posture?.Dispose();
        _posture = null;
        _backBridge?.Dispose();
        _backBridge = null;
        BackGestures = null;
        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }

        base.OnDestroy();
    }
}
