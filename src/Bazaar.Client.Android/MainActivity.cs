using Android.App;
using Android.Content.PM;
using Avalonia.Android;

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
}
