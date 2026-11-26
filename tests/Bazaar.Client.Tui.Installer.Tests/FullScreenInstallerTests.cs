using Bazaar.Client.Tui.TerminalGui;
using Bazaar.Client.Tui.Tests;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;
using Microsoft.Extensions.DependencyInjection;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Time;
using Terminal.Gui.Views;

namespace Bazaar.Client.Tui.Installer.Tests;

/// <summary>
/// The installer on the Terminal.Gui screen (doc 58 work package 3): the full-screen path opts in with a host factory, writes nothing
/// to the console while the screen is up, and ends the wizard cleanly on cancel. A scripted operator answers the first question and
/// cancels the second, over the ANSI driver with no real terminal.
/// </summary>
public sealed class FullScreenInstallerTests
{
    private static readonly IReadOnlyDictionary<string, string?> _noEnvironment = new Dictionary<string, string?>(StringComparer.Ordinal);

    private static TerminalEnvironment Env() => new(false, false, null, "xterm", []);

    /// <summary>A host whose loop is driven by a script: type "Example Studio" and Enter in the first dialog, Esc in every later one, Enter on the finished screen.</summary>
    private static Func<TerminalGuiHost> ScriptedHost(List<string> screens)
    {
        return () =>
        {
            var host = TerminalGuiHost.Start("ansi", new VirtualTimeProvider());
            var application = host.Application;
            application.Driver!.SetScreenSize(80, 24);
            var dialogs = 0;
            var wasDialog = false;
            var typed = 0;
            const string first = "Example Studio";
            application.Iteration += (_, _) =>
            {
                if (application.TopRunnableView is Dialog)
                {
                    if (!wasDialog)
                    {
                        dialogs++;
                        typed = 0;
                    }

                    wasDialog = true;
                    if (dialogs == 1 && typed < first.Length)
                    {
                        application.InjectKey(new Key(first[typed++]));
                    }
                    else
                    {
                        application.InjectKey(dialogs == 1 ? Key.Enter : Key.Esc);
                    }

                    return;
                }

                wasDialog = false;
                if (application.TopRunnableView is not null)
                {
                    application.TopRunnableView.SetNeedsDraw();
                    application.LayoutAndDraw(true);
                    screens.Add(application.Driver!.ToString());
                    application.InjectKey(Key.Enter);
                }
            };
            return host;
        };
    }

    [Fact]
    public async Task The_full_screen_path_runs_the_wizard_on_the_screen_and_stops_cleanly_on_cancel_without_touching_the_console()
    {
        var terminal = new ScriptedTerminal();
        var screens = new List<string>();
        var options = new TuiInstallerOptions(
            Env(),
            terminal,
            TuiInstaller.SharedBackend(_noEnvironment, new ClosedGate(), RegistrySetupSettingCatalog.Known),
            () => throw new InvalidOperationException("The plain path must not run."),
            FullScreenHostFactory: ScriptedHost(screens));

        var run = await TuiInstaller.RunAsync(options, TestContext.Current.CancellationToken);

        Assert.Equal(TuiMode.FullScreen, run.Mode.Mode);
        Assert.Equal((int)SetupExitCode.InvalidInput, run.Result.ExitCode);
        Assert.Null(run.Result.PlanHash);
        Assert.Equal(string.Empty, terminal.Output);
        var last = screens[^1];
        Assert.Contains("Bazaar server installer", last);
        Assert.Contains("Step 1 of", last);
        Assert.Contains("The wizard stopped before the review. Nothing was changed.", last);
    }

    [Fact]
    public async Task Without_a_host_factory_a_full_screen_run_keeps_the_console_widgets()
    {
        var terminal = new ScriptedTerminal().Lines("Example Studio");
        var options = new TuiInstallerOptions(
            Env(),
            terminal,
            TuiInstaller.SharedBackend(_noEnvironment, new ClosedGate(), RegistrySetupSettingCatalog.Known),
            () => throw new InvalidOperationException());

        var run = await TuiInstaller.RunAsync(options, TestContext.Current.CancellationToken);

        Assert.Equal(TuiMode.FullScreen, run.Mode.Mode);
        Assert.Contains("Step 1 of", terminal.Output, StringComparison.Ordinal);
    }

    private sealed class ClosedGate : ICliApplyGate
    {
        public bool DurableAuditStoreAvailable => false;

        public void RegisterApplyServices(IServiceCollection services) => throw new InvalidOperationException("A closed gate registers nothing.");
    }
}
