using System.Net.Http;
using Android.App;
using Android.Runtime;
using Android.Util;
using Avalonia;
using Avalonia.Android;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using Bazaar.Accessibility.Avalonia;
using Bazaar.Client.Core.Pairing;

namespace Bazaar.Client.Android.Gate;

/// <summary>
/// Runs the accessible-name scan on the Android head's first screen (the pairing view, doc 32 section 3) and logs one
/// result line for the CI step to read. The head builds that screen in App.OnFrameworkInitializationCompleted, which is
/// private, so this app builds the same view from the same Client.Core types with an empty credential store and no
/// network. The phone shell is not scanned here: it needs a paired endpoint, and the desktop gate already scans it.
/// </summary>
public sealed class GateApp : Avalonia.Application
{
    internal const string LogTag = "BazaarA11yGate";

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        var pairing = new PairingViewModel(
            new PairingFlow(new EmptyCredentialStore(), _ => new PairingClient(new HttpClient(), (_, _) => Task.CompletedTask)),
            deviceClass: "android");
        var root = new ContentControl { Content = new PairingView(pairing) };

        if (ApplicationLifetime is IActivityApplicationLifetime activity)
        {
            activity.MainViewFactory = () => root;
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            single.MainView = root;
        }

        base.OnFrameworkInitializationCompleted();

        // Scan once the view is in the tree. A scan that finds no interactive control would pass vacuously, so it fails too.
        Dispatcher.UIThread.Post(() => Report(root), DispatcherPriority.Loaded);
    }

    private static void Report(Control root)
    {
        int interactive = root.GetSelfAndLogicalDescendants().OfType<Control>()
            .Count(c => AccessibleControlScan.InteractiveTypes.Any(t => t.IsInstanceOfType(c)));
        IReadOnlyList<AccessibleControlProblem> problems = AccessibleControlScan.Find(root);

        if (interactive == 0)
        {
            Log.Error(LogTag, "no interactive controls found on the pairing view; the scan would be vacuous");
            Log.Error(LogTag, "RESULT=FAIL vacuous");
            return;
        }

        foreach (AccessibleControlProblem problem in problems)
        {
            Log.Error(LogTag, $"{problem.ControlType} name={problem.Name ?? "(none)"}: {problem.Problem}");
        }

        Log.Info(LogTag, $"checked {interactive} interactive controls");
        Log.Info(LogTag, problems.Count == 0 ? "RESULT=PASS" : $"RESULT=FAIL problems={problems.Count}");
    }
}

/// <summary>Application entry point for the gate app, mirroring BazaarAndroidApplication in the head.</summary>
[Application(Label = "Bazaar a11y gate")]
public class GateApplication : AvaloniaAndroidApplication<GateApp>
{
    public GateApplication(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }
}

/// <summary>Launcher activity. The explicit Name lets CI start it with a fixed component name.</summary>
[Activity(Name = "org.bazaar.client.accessibilitygate.MainActivity", MainLauncher = true, Exported = true)]
public class MainActivity : AvaloniaMainActivity
{
}

/// <summary>A credential store with nothing saved, so the pairing view starts unpaired.</summary>
internal sealed class EmptyCredentialStore : IRefreshCredentialStore
{
    public Task<string?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    public Task SaveAsync(string refreshCredential, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
