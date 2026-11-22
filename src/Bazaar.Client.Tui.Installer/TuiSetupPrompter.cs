using Bazaar.Server.Setup;

namespace Bazaar.Client.Tui.Installer;

/// <summary>
/// The installer TUI's <see cref="ISetupPrompter"/> (doc 58 work package 3). It owns only how a question is drawn: every step, check
/// and the plan hash stay in <see cref="SetupWizardEngine"/>, so a TUI run and a plain run with the same answers give the same plan.
/// Widgets come from <see cref="IPromptWidgets"/>, so a Terminal.Gui widget set can replace the console one. A secret goes straight
/// from the secret widget into a <see cref="SetupSecretInput"/>; it is never written to the terminal, a message or a log.
/// </summary>
public sealed class TuiSetupPrompter : ISetupPrompter
{
    private readonly IPromptWidgets _widgets;
    private readonly ITuiOutputSink _sink;

    /// <summary>The prompter over any widget set and any output sink (the full-screen path passes the Terminal.Gui pair).</summary>
    public TuiSetupPrompter(IPromptWidgets widgets, ITuiOutputSink sink)
    {
        ArgumentNullException.ThrowIfNull(widgets);
        ArgumentNullException.ThrowIfNull(sink);
        _widgets = widgets;
        _sink = sink;
    }

    /// <summary>The console prompter: output goes through <paramref name="terminal"/> as plain lines.</summary>
    public TuiSetupPrompter(IPromptWidgets widgets, ITerminal terminal, TuiStyle style)
        : this(widgets, new ConsoleOutputSink(terminal, style))
    {
    }

    public void ShowMessage(string line, SetupMessageKind kind = SetupMessageKind.Info)
    {
        ArgumentNullException.ThrowIfNull(line);
        _sink.WriteMessage(line, kind switch
        {
            SetupMessageKind.Error => TuiMessageKind.Error,
            SetupMessageKind.Warning => TuiMessageKind.Warning,
            _ => TuiMessageKind.Info,
        });
    }

    public void ShowProgress(SetupProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (progress.Total <= 0)
        {
            return;
        }

        // Only the question steps and the apply get a status line; Review and Finished are already announced by the engine's messages.
        if (progress.Phase is not (SetupProgressPhase.Questions or SetupProgressPhase.Applying))
        {
            return;
        }

        _sink.ShowProgress(new TuiProgress(progress.Current, progress.Total, progress.Title ?? ""));
    }

    public string? AskText(WizardQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        return _widgets.TextEntry(question.Prompt, question.Default);
    }

    public string? ChooseOne(WizardQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        return _widgets.PickOne(question.Prompt, question.Options, question.Default);
    }

    public IReadOnlyList<string>? ChooseMany(WizardQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var defaults = string.IsNullOrEmpty(question.Default)
            ? []
            : question.Default.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return _widgets.PickMany(question.Prompt, question.Options, defaults);
    }

    public bool? Confirm(WizardQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        return _widgets.YesNo(question.Prompt, string.Equals(question.Default, "yes", StringComparison.OrdinalIgnoreCase));
    }

    public SetupSecretInput? AskSecret(WizardQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var value = _widgets.SecretEntry(question.Prompt);
        return value is null ? null : new SetupSecretInput(value);
    }
}
