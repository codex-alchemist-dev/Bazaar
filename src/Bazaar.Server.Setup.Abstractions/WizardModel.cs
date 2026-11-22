namespace Bazaar.Server.Setup;

/// <summary>How a wizard question is answered. A UI picks its widget from this.</summary>
public enum WizardQuestionKind
{
    /// <summary>One line of text. Checked by <see cref="WizardQuestion.Validate"/>.</summary>
    Text,

    /// <summary>One of <see cref="WizardQuestion.Options"/>.</summary>
    Choice,

    /// <summary>Zero or more of <see cref="WizardQuestion.Options"/>.</summary>
    MultiChoice,

    /// <summary>Yes or no. <see cref="WizardQuestion.Default"/> is "yes" or "no".</summary>
    Confirm,

    /// <summary>A value that is stored in the secret store and never shown, logged or written to the plan. Only its reference reaches the plan.</summary>
    Secret,
}

/// <summary>
/// One wizard question, as data, so a console and a terminal UI render the same steps (doc 45 section 1.1). The engine
/// (<c>SetupWizardEngine</c>) owns the order, the re-asking and the plan path; a UI owns only how a question looks.
/// </summary>
public sealed record WizardQuestion
{
    /// <summary>A stable id, such as <c>install.companyName</c>. Tests and UIs key on it. It is not a plan path.</summary>
    public required string Id { get; init; }

    /// <summary>The question text, ready to show.</summary>
    public required string Prompt { get; init; }

    public required WizardQuestionKind Kind { get; init; }

    /// <summary>The answer an empty entry takes. Null means there is none. For <see cref="WizardQuestionKind.Confirm"/> it is "yes" or "no".</summary>
    public string? Default { get; init; }

    /// <summary>The plan key (dotted path) the answer is written to, the same key <c>--set</c> takes. Null when the answer is not a plan value.</summary>
    public string? PlanPath { get; init; }

    /// <summary>The allowed values for a choice question, in display order.</summary>
    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>One line that says what a valid answer looks like. It is shown when an answer is refused.</summary>
    public string? Hint { get; init; }

    /// <summary>True when an empty answer is accepted and writes nothing. Otherwise every answer, an empty one too, goes through <see cref="Validate"/>.</summary>
    public bool Optional { get; init; }

    /// <summary>Returns null when the text is acceptable, or the message to show.</summary>
    public Func<string, string?>? Validate { get; init; }

    /// <summary>A line shown before the question is asked.</summary>
    public string? Intro { get; init; }

    /// <summary>A line shown when an optional text question is left empty (and so writes nothing).</summary>
    public string? EmptyMessage { get; init; }

    /// <summary>
    /// A confirm question that is asked when this optional text question is left empty. A "yes" ends this question without a value;
    /// a "no" asks this question again.
    /// </summary>
    public WizardQuestion? IfEmpty { get; init; }

    /// <summary>For a confirm question: the plan value written for "yes". Null writes nothing.</summary>
    public string? PlanValueWhenYes { get; init; }

    /// <summary>For a confirm question: the plan value written for "no". Null writes nothing.</summary>
    public string? PlanValueWhenNo { get; init; }

    /// <summary>For a confirm question: a warning shown on "no".</summary>
    public string? NoMessage { get; init; }

    /// <summary>For a multi-choice question: turns the chosen options into the plan value.</summary>
    public Func<IReadOnlyList<string>, string>? JoinChoices { get; init; }

    /// <summary>For a secret question: the <c>secret://</c> reference the value is stored under, which is what the plan gets.</summary>
    public string? SecretReference { get; init; }

    /// <summary>Adjusts this question from the earlier answers (by question id), such as a default or a check that depends on a choice.</summary>
    public Func<IReadOnlyDictionary<string, string>, WizardQuestion>? Refine { get; init; }
}

/// <summary>A named group of questions, shown as one step of the progress bar.</summary>
public sealed record WizardStep(string Id, string Title, IReadOnlyList<WizardQuestion> Questions);
