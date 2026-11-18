using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.Constellation;
using Bazaar.Client.Constellation.Abstractions;
using Bazaar.Client.Core.Mobile;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Dashboard;
using Bazaar.Client.Gc;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.RyuseiFuda;
using Bazaar.Client.Vault;

namespace Bazaar.Client.Android;

public sealed class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // The client connects to the server it was built for (doc 37 section 4a). A release build without that identity fails here.
        // Provisional, pending spike S13: MainActivity declares configChanges, so rotation, fold and resize are handled in place and the view is not rebuilt (doc 29).
        // The Constellation pane fetches its tiles from the server over HTTP (POST /constellation/tiles).
        ClientServer.ConnectToCompanyServer();
        var host = new ContentControl
        {
            Content = CreatePhoneShell(HttpConstellationTileSource.Create(ClientServer.Current)),
        };

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
        // The panes this head ships, in pager order: the RyuseiFuda board first, then the Client.Core panes, then the rest of RyuseiFuda.
        // The Dashboard comes first, then Constellation, then the rest of the Client.Core panes (pager order).
        IPaneModule[] modules =
        [
            RyuseiFudaPanes.Board,
            new DashboardPaneModule(),
            new ConstellationPaneModule(),
            new GcPaneModule(),
            new VaultSearchPaneModule(),
            .. RyuseiFudaPanes.Modules,
        ];
        var features = new PaneModuleContext().With<IConstellationTileSource>(tiles);
        var registry = FirstPartyPanes.CreateRegistry(modules, features: features);
        return new PhoneShell(registry, FirstPartyPanes.PhonePaneBodies(registry, extra: kind => GcPaneModule.PhoneBody(kind, registry)));
    }
}

// CodeQL default setup retrigger: comment-only, no code change.

// CodeQL analysis trigger (no functional change).

// CodeQL analysis trigger (no functional change).

// CodeQL analysis trigger (no functional change).

// CI re-run: comment-only touch so checks report on a fresh head.

// CodeQL analysis trigger (no functional change).
// CodeQL analysis trigger, pass 2 (no functional change).

// CodeQL analysis trigger (comment only, no functional change).
