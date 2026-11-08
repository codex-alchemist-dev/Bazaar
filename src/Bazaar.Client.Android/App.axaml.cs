using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Bazaar.Client.Core.Mobile;

namespace Bazaar.Client.Android;

public sealed class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // TODO(pairing): call ClientServer.Use(endpoint) with the paired endpoint (doc 32). Until then the development default applies.
        // Provisional, pending spike S13: MainActivity declares configChanges, so rotation, fold and resize are handled in place and the view is not rebuilt (doc 29).
        if (ApplicationLifetime is IActivityApplicationLifetime activity)
        {
            activity.MainViewFactory = () => new PhoneShell();
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            single.MainView = new PhoneShell();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
