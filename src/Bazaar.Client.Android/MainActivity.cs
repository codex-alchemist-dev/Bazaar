using Android.App;
using Android.Content.PM;
using Avalonia.Android;

namespace Bazaar.Client.Android;

// Folding, unfolding and rotating must not recreate the activity: the phone switches layout variants locally (doc 29).
[Activity(
    Label = "Bazaar",
    Theme = "@style/Theme.Bazaar",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation
        | ConfigChanges.ScreenSize
        | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize
        | ConfigChanges.Density
        | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
}
