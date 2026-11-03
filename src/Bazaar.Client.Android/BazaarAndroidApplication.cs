using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace Bazaar.Client.Android;

[Application]
public class BazaarAndroidApplication : AvaloniaAndroidApplication<App>
{
    public BazaarAndroidApplication(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) => base.CustomizeAppBuilder(builder);
}
