using Bazaar.Server.Setup;

namespace Bazaar.Client.Tui.Installer;

/// <summary>
/// What the installer needs from its host. The host (a thin program or <c>bazaar-admin</c>) supplies the setup pieces it already has:
/// the backend factory, the optional secret writer, and the plain prompter for the fallback path. This module adds no setup logic.
/// </summary>
public sealed record TuiInstallerOptions(
    TerminalEnvironment Environment,
    ITerminal Terminal,
    Func<ISetupReportRenderer, ISetupWizardBackend> BackendFactory,
    Func<ISetupPrompter> PlainPrompterFactory,
    ISetupSecretWriter? SecretWriter = null);

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

    public static async Task<TuiInstallerRun> RunAsync(TuiInstallerOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var mode = TuiModeSelector.Select(options.Environment);
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
}
