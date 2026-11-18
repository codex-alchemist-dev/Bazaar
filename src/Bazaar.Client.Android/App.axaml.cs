using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.Constellation;
using Bazaar.Client.Constellation.Abstractions;
using Bazaar.Client.Mobile;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Dashboard;
using Bazaar.Client.Engines;
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

    // The shell follows the fold posture from the running activity. The bindings stay alive for the session.
    private readonly List<IDisposable> _postureBindings = [];

    private PhoneShell CreatePhoneShell(IConstellationTileSource tiles)
    {
        // The Dashboard comes first, then Constellation, then the rest of the Client.Core panes (pager order).
        IPaneModule[] modules =
        [
            RyuseiFudaPanes.Board,
            new DashboardPaneModule(),
            new ConstellationPaneModule(),
            new GcPaneModule(),
            new VaultSearchPaneModule(),
            .. RyuseiFudaPanes.Modules,
            new EnginePaneModule(),
        ];
        var features = new PaneModuleContext().With<IConstellationTileSource>(tiles);
        var registry = FirstPartyPanes.CreateRegistry(modules, features: features);
        var shell = new PhoneShell(registry, FirstPartyPanes.PhonePaneBodies(kind => RyuseiFudaPanes.PhoneBody(kind, registry), kind => GcPaneModule.PhoneBody(kind, registry)));
        var source = MainActivity.Current?.Posture;
        if (source is not null)
        {
            foreach (var old in _postureBindings)
            {
                old.Dispose();
            }
            _postureBindings.Clear();
            _postureBindings.Add(new PostureShellBinder(source, shell, WidthDp, new AndroidSettings().HalfOpenLayout));
        }
        return shell;
    }

    // Width of the display in dp. Zero before the activity exists, so the shell falls back to the folded layout.
    private static double WidthDp()
    {
        var metrics = MainActivity.Current?.Resources?.DisplayMetrics;
        return metrics is null ? 0 : metrics.WidthPixels / metrics.Density;
    }
}
