using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Bazaar.Client.Core.Constellation;
using Bazaar.Client.Core.Mobile;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Core.Server;
using Bazaar.Client.Mail;
using Bazaar.Client.RyuseiFuda;

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
        // The panes this head ships, in pager order: the RyuseiFuda board first, then the Client.Core panes, then the rest of RyuseiFuda.
        IPaneModule[] modules = [RyuseiFudaPanes.Board, .. FirstPartyPanes.Modules, .. RyuseiFudaPanes.Modules, .. MailPanes.Modules];
        var registry = FirstPartyPanes.CreateRegistry(modules, tiles: tiles);
        var shell = new PhoneShell(registry, FirstPartyPanes.PhonePaneBodies(registry));
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
