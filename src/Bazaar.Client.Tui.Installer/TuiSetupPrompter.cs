using System.Globalization;
using Bazaar.Server.Setup;

namespace Bazaar.Client.Tui.Installer;

/// <summary>
/// The installer TUI's <see cref="ISetupPrompter"/> (doc 58 work package 3). It owns only how a question is drawn: every step, check
/// and the plan hash stay in <see cref="SetupWizardEngine"/>, so a TUI run and a plain run with the same answers give the same plan.
/// Widgets come from <see cref="IPromptWidgets"/>, so a Terminal.Gui widget set can replace the console one. A secret goes straight
/// from the secret widget into a <see cref="SetupSecretInput"/>; it is never written to the terminal, a message or a log.
/// </summary>
public sealed class TuiSetupPrompter(IPromptWidgets widgets, ITerminal terminal, TuiStyle style) : ISetupPrompter
{
    private const int _maxBarWidth = 24;

    public void ShowMessage(string line, SetupMessageKind kind = SetupMessageKind.Info)
    {
        ArgumentNullException.ThrowIfNull(line);

        // The label is text, so colour is never the only signal (doc 58 section 4).
        terminal.WriteLine(kind switch
        {
            SetupMessageKind.Error => style.Error("Error: " + line),
            SetupMessageKind.Warning => style.Warning("Warning: " + line),
            _ => line,
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

        var width = Math.Min(_maxBarWidth, Math.Max(8, terminal.Width / 4));
        var filled = Math.Clamp(progress.Current * width / progress.Total, 0, width);
        var bar = "[" + new string('#', filled) + new string('-', width - filled) + "]";
        var count = string.Create(CultureInfo.InvariantCulture, $"Step {progress.Current} of {progress.Total}");
        terminal.WriteLine();
        terminal.WriteLine(style.Bold($"{bar} {count}: {progress.Title}"));
    }

    public string? AskText(WizardQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        return widgets.TextEntry(question.Prompt, question.Default);
    }

    public string? ChooseOne(WizardQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        return widgets.PickOne(question.Prompt, question.Options, question.Default);
    }

    public IReadOnlyList<string>? ChooseMany(WizardQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var defaults = string.IsNullOrEmpty(question.Default)
            ? []
            : question.Default.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return widgets.PickMany(question.Prompt, question.Options, defaults);
    }

    public bool? Confirm(WizardQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        return widgets.YesNo(question.Prompt, string.Equals(question.Default, "yes", StringComparison.OrdinalIgnoreCase));
    }

    public SetupSecretInput? AskSecret(WizardQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var value = widgets.SecretEntry(question.Prompt);
        return value is null ? null : new SetupSecretInput(value);
    }
}
