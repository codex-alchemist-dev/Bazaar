using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Bazaar.Client.Android.Pairing;
using Bazaar.Client.Core.Constellation;
using Bazaar.Client.Core.Mobile;
using Bazaar.Client.Core.Pairing;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Core.Server;

namespace Bazaar.Client.Android;

public sealed class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // The first screen is the pairing screen (doc 32, section 3). On success the paired endpoint is set and the phone shell
        // replaces it. The host stays the root view, so both lifetimes keep one stable root.
        // Provisional, pending spike S13: MainActivity declares configChanges, so rotation, fold and resize are handled in place and the view is not rebuilt (doc 29).
        // The Constellation pane fetches its tiles from the server over HTTP (POST /constellation/tiles).
        var host = new ContentControl();
        var flow = new PairingFlow(
            AndroidRefreshCredentials.Create(global::Android.App.Application.Context),
            uri => PairingClient.Create(uri));
        var pairing = new PairingViewModel(flow, deviceClass: "android");
        pairing.Paired += (_, completed) =>
        {
            ClientServer.Use(PairingClient.ToEndpoint(completed.BaseAddress, completed.Grant));
            host.Content = CreatePhoneShell(HttpConstellationTileSource.Create(ClientServer.Current));
        };
        host.Content = new PairingView(pairing);

        if (ApplicationLifetime is IActivityApplicationLifetime activity)
        {
            activity.MainViewFactory = () => host;
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            single.MainView = host;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static PhoneShell CreatePhoneShell(IConstellationTileSource tiles)
    {
        var registry = FirstPartyPanes.CreateRegistry(tiles: tiles);
        return new PhoneShell(registry, FirstPartyPanes.PhonePaneBodies(registry));
    }
}

// CodeQL default setup retrigger: comment-only, no code change.

// CodeQL analysis trigger (no functional change).

// CodeQL analysis trigger (no functional change).

// CodeQL analysis trigger (no functional change).

// CI re-run: comment-only touch so checks report on a fresh head.

// CodeQL analysis trigger (no functional change).
// CodeQL analysis trigger, pass 2 (no functional change).
