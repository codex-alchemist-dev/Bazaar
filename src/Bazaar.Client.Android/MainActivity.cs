using Android.App;
using Android.Content.PM;
using Android.Content.Res;
using Android.OS;
using Avalonia.Android;
using Bazaar.Client.Android.Posture;

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

    /// <summary>The running activity, or null before OnCreate and after OnDestroy. The app has one activity.</summary>
    public static MainActivity? Current { get; private set; }

    /// <summary>The fold posture source for this activity, or null before OnCreate and after OnDestroy.</summary>
    public WindowManagerPostureSource? Posture => _posture;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Current = this;
        _posture = new WindowManagerPostureSource(this);
    }

    // The source has no listener binding yet (see WindowManagerPostureSource), so the activity pulls the layout here.
    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        _posture?.Refresh();
    }

    protected override void OnDestroy()
    {
        _posture?.Dispose();
        _posture = null;
        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }

        base.OnDestroy();
    }
}
