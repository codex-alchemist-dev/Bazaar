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
/// Runs the accessible-name scan on the Android head's two screens: the pairing view (doc 32 section 3) and the phone shell
/// (doc 29) that replaces it after pairing. Both are built from the same Client.Core types as the head, with no network: the
/// pairing view starts with an empty credential store, and the phone shell gets a fake paired endpoint (GatePhoneShell).
/// It logs one combined result line for the CI step to read. The head builds its first screen in
/// App.OnFrameworkInitializationCompleted, which is private, so this app rebuilds it here.
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

        // Scan once the pairing view is in the tree. Then swap in the phone shell, as the head does after pairing, and scan it.
        Dispatcher.UIThread.Post(() =>
        {
            var pairingScan = GateViewScan.Of("pairing", root);
            root.Content = GatePhoneShell.Create();
            Dispatcher.UIThread.Post(() => Report([pairingScan, GateViewScan.Of("phone-shell", root)]), DispatcherPriority.Loaded);
        }, DispatcherPriority.Loaded);
    }

    private static void Report(IReadOnlyList<GateViewScan> views)
    {
        bool passed = true;
        foreach (GateViewScan view in views)
        {
            // A view with no interactive controls would pass vacuously, so it fails here.
            if (view.Interactive == 0)
            {
                Log.Error(LogTag, $"view={view.View}: no interactive controls found; the scan would be vacuous");
                passed = false;
                continue;
            }

            foreach (AccessibleControlProblem problem in view.Problems)
            {
                Log.Error(LogTag, $"view={view.View} {problem.ControlType} name={problem.Name ?? "(none)"}: {problem.Problem}");
            }

            Log.Info(LogTag, $"view={view.View} checked {view.Interactive} interactive controls, problems={view.Problems.Count}");
            passed &= view.Passed;
        }

        Log.Info(LogTag, passed ? "RESULT=PASS" : "RESULT=FAIL");
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
