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

namespace Bazaar.Client.Android.Gate;

/// <summary>
/// Runs the accessible-name scan on the Android head's phone shell (doc 29). The shell is built from the same Client.Core types as
/// the head, with no network: it gets a fake company endpoint (GatePhoneShell).
/// It logs one combined result line for the CI step to read. The head builds its first screen in
/// App.OnFrameworkInitializationCompleted, which is private, so this app rebuilds it here.
/// </summary>
public sealed class GateApp : Avalonia.Application
{
    internal const string LogTag = "BazaarA11yGate";

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        var root = new ContentControl { Content = GatePhoneShell.Create() };

        if (ApplicationLifetime is IActivityApplicationLifetime activity)
        {
            activity.MainViewFactory = () => root;
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            single.MainView = root;
        }

        base.OnFrameworkInitializationCompleted();

        // Scan the phone shell once it is in the tree.
        Dispatcher.UIThread.Post(() => Report([GateViewScan.Of("phone-shell", root)]), DispatcherPriority.Loaded);
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

