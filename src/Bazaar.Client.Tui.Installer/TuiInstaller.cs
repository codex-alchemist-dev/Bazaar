using Bazaar.Client.Tui.TerminalGui;
using Bazaar.Server.Setup;

namespace Bazaar.Client.Tui.Installer;

/// <summary>
/// What the installer needs from its host. The host (a thin program or <c>bazaar-admin</c>) supplies the setup pieces it already has:
/// the backend factory, the optional secret writer, and the plain prompter for the fallback path. This module adds no setup logic.
/// <paramref name="FullScreenHostFactory"/> opts in to the Terminal.Gui screen when the mode is full-screen (see
/// <see cref="TuiInstaller.TerminalGuiHostFactory"/>); without it a full-screen run uses the line-and-key console widgets over
/// <paramref name="Terminal"/>, as before.
/// </summary>
public sealed record TuiInstallerOptions(
    TerminalEnvironment Environment,
    ITerminal Terminal,
    Func<ISetupReportRenderer, ISetupWizardBackend> BackendFactory,
    Func<ISetupPrompter> PlainPrompterFactory,
    ISetupSecretWriter? SecretWriter = null,
    Func<TerminalGuiHost>? FullScreenHostFactory = null);

/// <summary>The result of an installer run: how it ran, and what the wizard returned.</summary>
public sealed record TuiInstallerRun(TuiModeDecision Mode, SetupWizardResult Result);

/// <summary>
/// The installer TUI entry (doc 58 section 2a): the full walkthrough every time, with no quick path. It picks the mode
/// (<see cref="TuiModeSelector"/>), builds the prompter for it, and hands everything to <see cref="SetupWizardEngine"/> with
/// <see cref="SetupWizardSteps.Create"/>. The plain fallback uses the host's own plain prompter, so the accessible path is unchanged.
/// </summary>
public static class TuiInstaller
{
    /// <summary>A backend factory over the shared <see cref="SetupWizardBackend"/> (same plan, hash and audit as <c>setup --dry-run</c> and apply).</summary>
    public static Func<ISetupReportRenderer, ISetupWizardBackend> SharedBackend(
        IReadOnlyDictionary<string, string?> environment,
        ICliApplyGate applyGate,
        ISetupSettingCatalog settings) =>
        renderer => new SetupWizardBackend(environment, applyGate, renderer, settings);

    /// <summary>A host factory for <see cref="TuiInstallerOptions.FullScreenHostFactory"/>. <paramref name="driverName"/> is a Terminal.Gui driver (<c>ansi</c>, <c>dotnet</c>, <c>windows</c>); null takes the platform default.</summary>
    public static Func<TerminalGuiHost> TerminalGuiHostFactory(string? driverName = null) => () => TerminalGuiHost.Start(driverName);

    public static async Task<TuiInstallerRun> RunAsync(TuiInstallerOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var mode = TuiModeSelector.Select(options.Environment);
        if (mode.Mode == TuiMode.FullScreen && options.FullScreenHostFactory is { } hostFactory)
        {
            return new TuiInstallerRun(mode, RunFullScreen(options, hostFactory, cancellationToken));
        }

        ISetupPrompter prompter;
        if (mode.Mode == TuiMode.FullScreen)
        {
            options.Terminal.WriteLine("Bazaar server installer. Up and Down choose, Enter accepts, Ctrl+C stops before anything is written.");
            prompter = new TuiSetupPrompter(new TerminalPromptWidgets(options.Terminal), options.Terminal, new TuiStyle(useColor: true));
        }
        else
        {
            prompter = options.PlainPrompterFactory();
        }

        var backend = options.BackendFactory(new TuiSetupReportRenderer());
        var engine = new SetupWizardEngine(prompter, backend, options.SecretWriter, SetupWizardSteps.Create());
        return new TuiInstallerRun(mode, await engine.RunAsync(cancellationToken));
    }

    // The screen runs on this thread (Terminal.Gui belongs to the thread that started it) and the wizard on a worker task, so the
    // wizard's own awaits can never move a draw call to another thread. The console is not touched while the screen is up.
    private static SetupWizardResult RunFullScreen(TuiInstallerOptions options, Func<TerminalGuiHost> hostFactory, CancellationToken cancellationToken)
    {
        using var host = hostFactory();
        using var screen = host.CreateInstallerScreen();
        var prompter = new TuiSetupPrompter(screen.Widgets, screen.Sink);
        screen.Sink.WriteMessage("Bazaar server installer. Up and Down choose, Enter accepts, Esc or Ctrl+C stops before anything is written.");
        var backend = options.BackendFactory(new TuiSetupReportRenderer());
        var engine = new SetupWizardEngine(prompter, backend, options.SecretWriter, SetupWizardSteps.Create());
        return screen.Run(() => engine.RunAsync(cancellationToken));
    }
}
