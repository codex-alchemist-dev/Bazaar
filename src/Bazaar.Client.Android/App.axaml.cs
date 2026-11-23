using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Bazaar.Accessibility;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.Android.Annotation;
using Bazaar.Client.Android.Approvals;
using Bazaar.Client.Android.Wire;
using Bazaar.Client.Approvals;
using Bazaar.Client.Approvals.Phone;
using Bazaar.Client.TalentPool;
using Bazaar.Client.Calendar;
using Bazaar.Client.Constellation;
using Bazaar.Client.Constellation.Abstractions;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Analytics;
using Bazaar.Client.Mail;
using Bazaar.Client.Dashboard;
using Bazaar.Client.Engines;
using Bazaar.Client.Gc;
using Bazaar.Client.GitSolarSystem.Abstractions;
using Bazaar.Client.GitSolarSystem.Http;
using Bazaar.Client.GitSolarSystem.Panes;
using Bazaar.Client.Interrupts;
using Bazaar.Client.Mobile;
using Bazaar.Client.Modules;
using Bazaar.Client.Navigation;
using Bazaar.Client.Navigation.Abstractions;
using Bazaar.Client.Preferences;
using Bazaar.Client.Preferences.Abstractions;
using Bazaar.Client.Org;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.RyuseiFuda;
using Bazaar.Client.RyuseiFuda.Phone;
using Bazaar.Client.Shell;
using Bazaar.Contracts.Panes;
using Bazaar.Client.Vault;
using Bazaar.Client.Views;
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
        _wire = AndroidWire.Start(
            ClientServer.Current,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "bazaar"),
            "bazaar-android");
        var host = new ContentControl
        {
            Content = CreatePhoneShell(HttpConstellationTileSource.Create(ClientServer.Current)),
        };
        Annotation = new AndroidAnnotation(host, ClientServer.Current);

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
            // What a chosen command does belongs to the pane that owns it; until a pane claims one, the head only records it.
            _mnemonics.CommandInvoked += (_, id) => Console.Error.WriteLine($"Bazaar: mnemonic command {id}");
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

    // The one socket to the company server. Kanban (and later approvals) send their wire requests through it.
    private AndroidWire? _wire;

    /// <summary>
    /// The project whose board the Kanban pane shows, or null while none is chosen (the pane then says so and sends nothing).
    /// How the phone learns the project is not decided yet (doc 29 and doc 12 name no project listing), so a build sets it.
    /// </summary>
    public static Guid? KanbanProjectId { get; set; }

    /// <summary>
    /// Screenshot annotation over the HTTP blob channel (doc 29, work package 11). The radial menu is not in the head yet, so its
    /// Annotate sector will call <see cref="AndroidAnnotation.ShowAsync"/> once it is.
    /// </summary>
    public AndroidAnnotation? Annotation { get; private set; }

    // Push (doc 29, Background delivery) is not wired. PushProviderSelector and BareWakeHandler exist in Bazaar.Client.Push, but the
    // pieces they need do not: no IPushProvider for FCM or UnifiedPush, no IWakeFetcher and no ILocalNotifier. Once those exist, build
    // `new PushProviderSelector(providers).Select(settings.PushProvider)` and pass wakes to `new BareWakeHandler(fetcher, notifier).HandleAsync(rawKind, ct)`.

    private PhoneShell CreatePhoneShell(IConstellationTileSource tiles)
    {
        // The Dashboard comes first, then Constellation, then the rest of the Client.Core panes, then Git Solar System (pager order).
        IPaneModule[] modules =
        [
            RyuseiFudaPanes.Board,
            new DashboardPaneModule(),
            new ConstellationPaneModule(),
            new GcPaneModule(),
            new ApprovalsPaneModule(),
            new VaultSearchPaneModule(),
            .. RyuseiFudaPanes.Modules,
            .. OrgPanes.Modules,
            .. CalendarPanes.Modules,
            .. InterruptPanes.Modules,
            new EnginePaneModule(),
            .. GitSolarSystemPanes.Modules,
            new AgentAnalyticsPaneModule(),
            .. MailPanes.Modules,
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
        // The approvals pane gets the biometric gate and the server source. With no enrolled approval key the gate is absent and the pane says so (doc 29 Approvals).
        var deviceId = MainActivity.Current?.GetSharedPreferences("bazaar", global::Android.Content.FileCreationMode.Private)?.GetString("approval_device_id", null);
        var (gate, approvals) = AndroidApprovals.Create(ClientServer.Current, () => MainActivity.Current, deviceId);
        // Talent tool grants ride the same pane, key and biometric. No enrolled owner id means no gate and no cards (doc 25, doc 32).
        var ownerHumanId = MainActivity.Current?.GetSharedPreferences("bazaar", global::Android.Content.FileCreationMode.Private)?.GetString("owner_human_id", null);
        var (grantGate, grants) = AndroidApprovals.CreateToolGrants(ClientServer.Current, () => MainActivity.Current, deviceId, ownerHumanId);
        // The Git Solar System feed has no Client.Core type, so the head hands it over through PaneModuleContext.With.
        var features = new PaneModuleContext()
            .With<IConstellationTileSource>(tiles)
            .With<IPendingApprovalSource>(approvals)
            .With<IPendingToolGrantSource>(grants)
            .With<IGitSolarSource>(HttpGitSolarSource.Create(ClientServer.Current))
            // The head supplies the view context; a module with view documents renders them, others keep their code-built panes (doc 41c section 11).
            .WithViewContext(ViewContextServices.CreateDefault(viewActions, ex => Console.Error.WriteLine($"Bazaar: view action failed: {ex.Message}")));
        if (gate is not null) features = features.With(gate);
        if (grantGate is not null) features = features.With(grantGate);
        // Every module's manifest is checked the same way (doc 41 section 8); a module that fails adds nothing.
        var loaded = ClientModuleHost.Load(modules.OfType<IClientModule>(), ClientHostFacts.Current(), Console.Error.WriteLine);
        _moduleCommands = loaded.Commands;
        var shipped = loaded.Only(modules);
        var registry = FirstPartyPanes.CreateRegistry(shipped, features: features);
        var settings = new AndroidSettings();
        // The Kanban pane is the live board over the wire (doc 29 work package 9), not the static placeholder body.
        var board = new WireKanbanBoardClient(_wire!.Client);
        var shell = new PhoneShell(
            registry,
            FirstPartyPanes.PhonePaneBodies(
                kind => kind == PaneKinds.Kanban ? KanbanPhoneBody.Create(board, KanbanProjectId) : null,
                kind => RyuseiFudaPanes.PhoneBody(kind, registry),
                kind => GcPaneModule.PhoneBody(kind, registry),
                kind => ApprovalsPaneModule.PhoneBody(kind, registry)),
            settings.Gestures,
            settings.RadialSectors);
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
            _postureBindings.Add(new PostureShellBinder(source, shell, WidthDp, settings.HalfOpenLayout));
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
