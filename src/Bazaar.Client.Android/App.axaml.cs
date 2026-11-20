using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.Android.Annotation;
using Bazaar.Client.Android.Approvals;
using Bazaar.Client.Android.Wire;
using Bazaar.Client.Constellation;
using Bazaar.Client.Constellation.Abstractions;
using Bazaar.Client.Core.Mobile;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Dashboard;
using Bazaar.Client.Gc;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.RyuseiFuda;
using Bazaar.Client.RyuseiFuda.Phone;
using Bazaar.Contracts.Panes;
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
        _wire = AndroidWire.Start(
            ClientServer.Current,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "bazaar"),
            "bazaar-android");
        var host = new ContentControl
        {
            Content = CreatePhoneShell(HttpConstellationTileSource.Create(ClientServer.Current)),
        };
        Approvals = new AndroidApprovals(() => MainActivity.Current);
        Annotation = new AndroidAnnotation(host, ClientServer.Current);

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

    // The one socket to the company server. Kanban (and later approvals) send their wire requests through it.
    private AndroidWire? _wire;

    /// <summary>
    /// The project whose board the Kanban pane shows, or null while none is chosen (the pane then says so and sends nothing).
    /// How the phone learns the project is not decided yet (doc 29 and doc 12 name no project listing), so a build sets it.
    /// </summary>
    public static Guid? KanbanProjectId { get; set; }

    /// <summary>
    /// The approval path: biometric prompt, signer and gate (doc 29, Approvals). Approval cards are built with
    /// <see cref="AndroidApprovals.CreateCardView"/> so they decide only through the gate. No pane lists cards yet: there is
    /// no approvals pane kind or wire type for cards or signed decisions, and the approval key cannot be enrolled (doc 32 Question 4).
    /// </summary>
    public AndroidApprovals? Approvals { get; private set; }

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
        var firstParty = FirstPartyPanes.PhonePaneBodies(registry, extra: kind => GcPaneModule.PhoneBody(kind, registry));
        // The Kanban pane is the live board over the wire (doc 29 work package 9), not the static placeholder body.
        var board = new WireKanbanBoardClient(_wire!.Client);
        var shell = new PhoneShell(registry, kind => kind == PaneKinds.Kanban
            ? KanbanPhoneBody.Create(board, KanbanProjectId)
            : firstParty(kind));
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

// CodeQL default setup retrigger: comment-only, no code change.

// CodeQL analysis trigger (no functional change).

// CodeQL analysis trigger (no functional change).

// CodeQL analysis trigger (no functional change).

// CI re-run: comment-only touch so checks report on a fresh head.

// CodeQL analysis trigger (no functional change).
// CodeQL analysis trigger, pass 2 (no functional change).

// CodeQL analysis trigger (comment only, no functional change).
