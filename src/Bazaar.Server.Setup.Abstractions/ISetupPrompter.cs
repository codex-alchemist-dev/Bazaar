namespace Bazaar.Server.Setup;

/// <summary>How serious a line shown to the operator is. A console ignores it; a terminal UI may colour it.</summary>
public enum SetupMessageKind
{
    Info,
    Warning,
    Error,
}

/// <summary>Where the wizard is, for a progress bar.</summary>
public enum SetupProgressPhase
{
    Questions,
    Review,
    Applying,
    Finished,
}

/// <summary>
/// A progress report. <see cref="Current"/> counts from 1 over <see cref="Total"/> steps. <see cref="StepId"/> is the wizard step
/// id during the questions, and null in the other phases.
/// </summary>
public sealed record SetupProgress(SetupProgressPhase Phase, int Current, int Total, string? StepId, string? Title);

/// <summary>
/// A secret the operator typed. It lives in memory only: the engine reads it once to store it, and <see cref="Dispose"/> overwrites
/// the buffer. It has no ToString that shows the value.
/// </summary>
public sealed class SetupSecretInput : IDisposable
{
    private readonly char[] _value;

    public SetupSecretInput(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = value.ToCharArray();
    }

    /// <summary>The value, for the one call that stores it. Do not log it, show it or keep it.</summary>
    public string Reveal() => new(_value);

    public void Dispose() => Array.Clear(_value);

    public override string ToString() => "(secret)";
}

/// <summary>
/// The wizard's conversation with the operator (doc 45 section 1.1). It is the port between the UI-free
/// <c>SetupWizardEngine</c> and a console or a terminal UI. Each ask returns null when the input has ended or the operator
/// cancelled, which stops the wizard before anything is written. A prompter owns how a question is drawn and how a bad entry is
/// re-prompted within its own widget (an unknown choice, an unreadable number list); the engine owns text validation and the plan.
/// </summary>
public interface ISetupPrompter
{
    /// <summary>Shows one line.</summary>
    void ShowMessage(string line, SetupMessageKind kind = SetupMessageKind.Info);

    /// <summary>Reports progress. A prompter that has no progress display ignores it.</summary>
    void ShowProgress(SetupProgress progress);

    /// <summary>Asks for text. An empty entry returns <see cref="WizardQuestion.Default"/>, or "" when there is none.</summary>
    string? AskText(WizardQuestion question);

    /// <summary>Asks for one of <see cref="WizardQuestion.Options"/>. Returns the option as listed. An empty entry takes the default.</summary>
    string? ChooseOne(WizardQuestion question);

    /// <summary>Asks for any number of <see cref="WizardQuestion.Options"/>, possibly none. Returns them in the order listed.</summary>
    IReadOnlyList<string>? ChooseMany(WizardQuestion question);

    /// <summary>Asks a yes or no question. An empty entry takes the default.</summary>
    bool? Confirm(WizardQuestion question);

    /// <summary>Asks for a secret without showing it. The caller disposes the result.</summary>
    SetupSecretInput? AskSecret(WizardQuestion question);
}
