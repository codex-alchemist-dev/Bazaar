using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.Constellation;
using Bazaar.Client.Constellation.Abstractions;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Dashboard;
using Bazaar.Client.Engines;
using Bazaar.Client.Gc;
using Bazaar.Client.Mobile;
using Bazaar.Client.Modules;
using Bazaar.Client.Navigation;
using Bazaar.Client.Navigation.Abstractions;
using Bazaar.Client.Preferences;
using Bazaar.Client.Preferences.Abstractions;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.RyuseiFuda;
using Bazaar.Client.Vault;
using Bazaar.Client.Views;
using Bazaar.Contracts.Modules;

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
        // The four view actions (doc 41c section 6) go to the navigator, the command runner and the preference store. The surface is bound
        // once the phone shell exists. Preferences stay on this device until a wire sync is attached.
        var paneSurface = new LatePaneSurface();
        var preferences = new PreferenceStore(
            modules.OfType<IPreferenceDeclarer>().SelectMany(m => m.Preferences),
            new FilePreferenceStorage(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bazaar", "preferences.json"),
                Console.Error.WriteLine));
        var viewActions = ViewActionRouter.ForServices(new PaneNavigator(paneSurface), new CommandRunner(), preferences);
        var features = new PaneModuleContext()
            .With<IConstellationTileSource>(tiles)
            .WithViewContext(ViewContextServices.CreateDefault(viewActions, ex => Console.Error.WriteLine($"Bazaar: view action failed: {ex.Message}")));
        // Every module's manifest is checked the same way (doc 41 section 8); a module that fails adds nothing.
        var shipped = ClientModuleHost.Load(modules.OfType<IClientModule>(), ClientHostFacts.Current(), Console.Error.WriteLine).Only(modules);
        var registry = FirstPartyPanes.CreateRegistry(shipped, features: features);
        var shell = new PhoneShell(registry, FirstPartyPanes.PhonePaneBodies(kind => RyuseiFudaPanes.PhoneBody(kind, registry), kind => GcPaneModule.PhoneBody(kind, registry)));
        paneSurface.Bind(
            registry.Contains,
            (kind, _) => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                int page = shell.Pages.ToList().FindIndex(p => p.Contains(kind, StringComparer.Ordinal));
                if (page < 0)
                {
                    throw new NavigationException($"The pane '{kind}' is not on a page of this shell.");
                }

                shell.GoToPage(page);
            }).GetTask());
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
