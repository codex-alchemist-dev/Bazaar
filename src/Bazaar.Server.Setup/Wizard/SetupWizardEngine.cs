using System.Globalization;

namespace Bazaar.Server.Setup;

/// <summary>
/// The setup wizard without a screen (doc 45 section 1.1). It walks the ordered question model through an
/// <see cref="ISetupPrompter"/>, turns each answer into a <c>path=value</c> flag (the form <c>setup --set</c> takes), then asks the
/// <see cref="ISetupWizardBackend"/> for a dry run, which validates and hashes the plan the way every other door does. It applies,
/// with entry path <see cref="SetupEntryPath.Wizard"/>, only when the operator types the start of the plan hash the review printed.
/// It never writes a file and never touches the console: a console and a terminal UI are two prompters over this one engine.
/// </summary>
public sealed class SetupWizardEngine
{
    private readonly ISetupPrompter _prompter;
    private readonly ISetupWizardBackend _backend;
    private readonly ISetupSecretWriter? _secretWriter;
    private readonly IReadOnlyList<WizardStep> _steps;
    private readonly List<string> _sets = [];
    private readonly Dictionary<string, string> _answers = new(StringComparer.Ordinal);

    public SetupWizardEngine(
        ISetupPrompter prompter,
        ISetupWizardBackend backend,
        ISetupSecretWriter? secretWriter = null,
        IReadOnlyList<WizardStep>? steps = null)
    {
        ArgumentNullException.ThrowIfNull(prompter);
        ArgumentNullException.ThrowIfNull(backend);
        _prompter = prompter;
        _backend = backend;
        _secretWriter = secretWriter;
        _steps = steps ?? SetupWizardSteps.Create();
    }

    /// <summary>Runs the questions, then the review, then the optional apply. Ends early (exit 1) when the input ends.</summary>
    public async Task<SetupWizardResult> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await RunQuestionsAndReviewAsync(cancellationToken);
        }
        catch (InputEndedException)
        {
            _prompter.ShowMessage("The wizard stopped before the review. Nothing was changed.");
            return Finish((int)SetupExitCode.InvalidInput, null);
        }
        catch (SecretStoreUnavailableException)
        {
            _prompter.ShowMessage("A secret was asked for, but no secret store is available in this build. Nothing was changed.", SetupMessageKind.Error);
            return Finish((int)SetupExitCode.InvalidInput, null);
        }
    }

    private async Task<SetupWizardResult> RunQuestionsAndReviewAsync(CancellationToken cancellationToken)
    {
        _prompter.ShowMessage("Bazaar server setup. Answer each question. Press Enter to take the value in brackets.");
        _prompter.ShowMessage("Nothing is written until the review is shown and you type the start of its plan hash.");

        // The plan's schema version is not a question. It is set the way every door sets it, so the plan hash matches.
        Set("schemaVersion", SetupConstants.SchemaVersion.ToString(CultureInfo.InvariantCulture));

        for (var index = 0; index < _steps.Count; index++)
        {
            var step = _steps[index];
            _prompter.ShowProgress(new SetupProgress(SetupProgressPhase.Questions, index + 1, _steps.Count, step.Id, step.Title));
            foreach (var question in step.Questions)
            {
                await AskQuestionAsync(question, cancellationToken);
            }
        }

        return await ReviewAndApplyAsync(cancellationToken);
    }

    /// <summary>Shows the same checks as <c>setup --dry-run</c>, then offers the apply. Holds and errors stop before the offer.</summary>
    private async Task<SetupWizardResult> ReviewAndApplyAsync(CancellationToken cancellationToken)
    {
        var total = _steps.Count;
        _prompter.ShowProgress(new SetupProgress(SetupProgressPhase.Review, total, total, null, "Review"));
        _prompter.ShowMessage(string.Empty);
        _prompter.ShowMessage("Review. These are the same checks as setup validate and setup --dry-run. Nothing has been written.");
        var dryRun = _backend.DryRun(_sets);
        Emit(dryRun);

        if (dryRun.ExitCode == (int)SetupExitCode.Held)
        {
            _prompter.ShowMessage("A held item needs approval before it can apply. Nothing was changed.");
            return Finish(dryRun.ExitCode, dryRun.PlanHash);
        }

        if (dryRun.ExitCode != (int)SetupExitCode.Ok || dryRun.PlanHash is null)
        {
            _prompter.ShowMessage("Fix the errors above and run the wizard again. Nothing was changed.");
            return Finish(dryRun.ExitCode, dryRun.PlanHash);
        }

        var typed = (_prompter.AskText(SetupWizardSteps.PlanHashQuestion(dryRun.PlanHash)) ?? throw new InputEndedException()).Trim();
        if (typed.Length == 0)
        {
            _prompter.ShowMessage("Stopped without applying. Nothing was changed.");
            return Finish((int)SetupExitCode.Ok, dryRun.PlanHash);
        }

        if (!SetupWizardSteps.MatchesHashPrefix(typed, dryRun.PlanHash))
        {
            _prompter.ShowMessage("The text does not match the start of the plan hash. Nothing was applied. Run the wizard again to review the plan.");
            return Finish((int)SetupExitCode.InvalidInput, dryRun.PlanHash);
        }

        _prompter.ShowProgress(new SetupProgress(SetupProgressPhase.Applying, total, total, null, "Applying"));
        var applied = await _backend.ApplyAsync(_sets, dryRun.PlanHash, SetupEntryPath.Wizard, cancellationToken);
        Emit(applied);
        _prompter.ShowProgress(new SetupProgress(SetupProgressPhase.Finished, total, total, null, "Finished"));
        return Finish(applied.ExitCode, applied.PlanHash);
    }

    private async Task AskQuestionAsync(WizardQuestion declared, CancellationToken cancellationToken)
    {
        var question = declared.Refine?.Invoke(_answers) ?? declared;
        if (question.Intro is not null)
        {
            _prompter.ShowMessage(question.Intro);
        }

        switch (question.Kind)
        {
            case WizardQuestionKind.Text:
                AskText(question);
                break;
            case WizardQuestionKind.Choice:
                AskChoice(question);
                break;
            case WizardQuestionKind.MultiChoice:
                AskMany(question);
                break;
            case WizardQuestionKind.Confirm:
                AskConfirm(question);
                break;
            case WizardQuestionKind.Secret:
                await AskSecretAsync(question, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(declared), question.Kind, "Unknown question kind.");
        }
    }

    /// <summary>Asks until the answer passes the question's check. An optional question may be left empty, and may then ask a follow-up.</summary>
    private void AskText(WizardQuestion question)
    {
        while (true)
        {
            var text = _prompter.AskText(question) ?? throw new InputEndedException();
            if (text.Length == 0 && question.Optional)
            {
                if (question.EmptyMessage is not null)
                {
                    _prompter.ShowMessage(question.EmptyMessage);
                }

                if (question.IfEmpty is null)
                {
                    return;
                }

                if (AskConfirm(question.IfEmpty))
                {
                    return;
                }

                continue;
            }

            var problem = question.Validate?.Invoke(text);
            if (problem is not null)
            {
                _prompter.ShowMessage(problem, SetupMessageKind.Error);
                continue;
            }

            Record(question, text);
            return;
        }
    }

    private void AskChoice(WizardQuestion question)
    {
        var chosen = _prompter.ChooseOne(question) ?? throw new InputEndedException();
        Record(question, chosen);
    }

    private void AskMany(WizardQuestion question)
    {
        var chosen = _prompter.ChooseMany(question) ?? throw new InputEndedException();
        var value = question.JoinChoices is null ? string.Join(",", chosen) : question.JoinChoices(chosen);
        Record(question, value);
    }

    /// <summary>Returns the answer. "yes" writes <see cref="WizardQuestion.PlanValueWhenYes"/> and "no" writes the other, when they are set.</summary>
    private bool AskConfirm(WizardQuestion question)
    {
        var yes = _prompter.Confirm(question) ?? throw new InputEndedException();
        _answers[question.Id] = yes ? "yes" : "no";
        if (question.PlanPath is not null)
        {
            var value = yes ? question.PlanValueWhenYes : question.PlanValueWhenNo;
            if (value is not null)
            {
                Set(question.PlanPath, value);
            }
        }

        if (!yes && question.NoMessage is not null)
        {
            _prompter.ShowMessage(question.NoMessage, SetupMessageKind.Warning);
        }

        return yes;
    }

    /// <summary>
    /// Stores the secret under the question's reference and writes only the reference to the plan. The value is read once, handed to
    /// the writer and wiped. It is never put in a flag, a message or an exception text.
    /// </summary>
    private async Task AskSecretAsync(WizardQuestion question, CancellationToken cancellationToken)
    {
        if (_secretWriter is not { StoreAvailable: true } || question.SecretReference is null || question.PlanPath is null)
        {
            throw new SecretStoreUnavailableException();
        }

        using var secret = _prompter.AskSecret(question) ?? throw new InputEndedException();
        try
        {
            await _secretWriter.WriteAsync(question.SecretReference, secret.Reveal(), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // No detail from the writer is shown: it could carry the value.
            throw new SecretStoreUnavailableException();
        }

        Set(question.PlanPath, question.SecretReference);
        _answers[question.Id] = question.SecretReference;
    }

    private void Record(WizardQuestion question, string value)
    {
        _answers[question.Id] = value;
        if (question.PlanPath is not null)
        {
            Set(question.PlanPath, value);
        }
    }

    /// <summary>Records one answer as a <c>path=value</c> flag, the same form the flags door takes.</summary>
    private void Set(string path, string value)
    {
        _sets.Add(path + "=" + value);
    }

    private SetupWizardResult Finish(int exitCode, string? planHash) => new(exitCode, _sets.ToList(), planHash);

    private void Emit(SetupWizardReview review)
    {
        foreach (var line in review.Lines)
        {
            _prompter.ShowMessage(line);
        }
    }

    /// <summary>Thrown when the input ends. The wizard then stops without applying anything.</summary>
    private sealed class InputEndedException : Exception
    {
    }

    /// <summary>Thrown when a secret cannot be stored. It carries no text, so nothing typed can leak through it.</summary>
    private sealed class SecretStoreUnavailableException : Exception
    {
    }
}
