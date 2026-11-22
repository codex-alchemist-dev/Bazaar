using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Bazaar.Accessibility;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.Commands;
using Bazaar.Client.Constellation;
using Bazaar.Client.Constellation.Abstractions;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Dashboard;
using Bazaar.Client.Engines;
using Bazaar.Client.Gc;
using Bazaar.Client.GitSolarSystem.Abstractions;
using Bazaar.Client.GitSolarSystem.Http;
using Bazaar.Client.GitSolarSystem.Panes;
using Bazaar.Client.Mobile;
using Bazaar.Client.Modules;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.RyuseiFuda;
using Bazaar.Client.Shell;
using Bazaar.Client.Vault;
using Bazaar.Contracts.Modules;
using Bazaar.Ex.Head;
using Bazaar.Localisation;

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

        // Direction from the locale and the screen-reader live region wrap the main view (doc 41c; Android has no window).
        ShellHost.Install(host, new SystemLocaleSource(), new AnnouncementGate(TimeProvider.System));

        // Mnemonics serve a hardware keyboard (a foldable or a docked phone). They start once the view has a top level to listen on.
        host.AttachedToVisualTree += (_, _) => _ = InstallMnemonicsAsync(host);

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

    private MnemonicShell? _mnemonics;
    private readonly CommandRouter _router = new();
    private readonly LatePaneNavigator _navigator = new();

    private async Task InstallMnemonicsAsync(ContentControl host)
    {
        if (_mnemonics is not null || TopLevel.GetTopLevel(host) is not { } top)
        {
            return;
        }

        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bazaar", MnemonicHeadSetup.OverridesFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _mnemonics = await MnemonicHeadSetup.InstallAsync(top, host, _moduleCommands, path, Console.Error.WriteLine);
            // What a chosen command does belongs to the pane module that registered a handler for it.
            _mnemonics.CommandInvoked += (_, id) =>
            {
                if (!_router.TryInvoke(id))
                {
                    Console.Error.WriteLine($"Bazaar: no pane handles mnemonic command {id}");
                }
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Bazaar: keyboard mnemonics are off: {ex.Message}");
        }
    }

    // The commands the loaded client modules contributed, kept for the mnemonic shell.
    private IReadOnlyList<ClientCommandContribution> _moduleCommands = [];

    // The shell follows the fold posture from the running activity. The bindings stay alive for the session.
    private readonly List<IDisposable> _postureBindings = [];

    private PhoneShell CreatePhoneShell(IConstellationTileSource tiles)
    {
        // The Dashboard comes first, then Constellation, then the rest of the Client.Core panes, then Git Solar System (pager order).
        IPaneModule[] modules =
        [
            RyuseiFudaPanes.Board,
            new DashboardPaneModule(),
            new ConstellationPaneModule(),
            new GcPaneModule(),
            new VaultSearchPaneModule(),
            .. RyuseiFudaPanes.Modules,
            new EnginePaneModule(),
            .. GitSolarSystemPanes.Modules,
        ];
        // The Git Solar System feed has no Client.Core type, so the head hands it over through PaneModuleContext.With.
        var features = new PaneModuleContext()
            .With<ICommandRouter>(_router)
            .With<IPaneNavigator>(_navigator)
            .With<IConstellationTileSource>(tiles)
            .With<IGitSolarSource>(HttpGitSolarSource.Create(ClientServer.Current));
        // Every module's manifest is checked the same way (doc 41 section 8); a module that fails adds nothing.
        var loaded = ClientModuleHost.Load(modules.OfType<IClientModule>(), ClientHostFacts.Current(), Console.Error.WriteLine);
        _moduleCommands = loaded.Commands;
        var shipped = loaded.Only(modules);
        var registry = FirstPartyPanes.CreateRegistry(shipped, features: features);
        var shell = new PhoneShell(registry, FirstPartyPanes.PhonePaneBodies(kind => RyuseiFudaPanes.PhoneBody(kind, registry), kind => GcPaneModule.PhoneBody(kind, registry)));
        _navigator.Bind(shell.ShowPane);
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
