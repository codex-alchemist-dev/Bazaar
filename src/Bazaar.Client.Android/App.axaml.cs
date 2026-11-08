using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Bazaar.Client.Core.Constellation;
using Bazaar.Client.Core.Mobile;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Core.Server;

namespace Bazaar.Client.Android;

public sealed class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // TODO(pairing): call ClientServer.Use(endpoint) with the paired endpoint (doc 32). Until then the development default applies.
        // Provisional, pending spike S13: MainActivity declares configChanges, so rotation, fold and resize are handled in place and the view is not rebuilt (doc 29).
        // The Constellation pane fetches its tiles from the server over HTTP (POST /constellation/tiles).
        var tiles = HttpConstellationTileSource.Create(ClientServer.Current);
        if (ApplicationLifetime is IActivityApplicationLifetime activity)
        {
            activity.MainViewFactory = () => new PhoneShell(FirstPartyPanes.CreateRegistry(tiles: tiles));
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            single.MainView = new PhoneShell(FirstPartyPanes.CreateRegistry(tiles: tiles));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
