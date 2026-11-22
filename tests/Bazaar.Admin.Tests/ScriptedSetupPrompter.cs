using Bazaar.Server.Setup;

namespace Bazaar.Admin.Tests;

/// <summary>
/// A prompter that answers from a list, in order, one answer per question. An empty answer takes the default, as the console does,
/// and a bad choice is asked again. It records every question and every line the wizard says. When the answers run out, the input
/// has ended, which stops the wizard.
/// </summary>
internal sealed class ScriptedSetupPrompter(IEnumerable<string> answers) : ISetupPrompter
{
    private readonly Queue<string> _answers = new(answers);

    /// <summary>Every question asked, in order, with its text only (the default is not recorded).</summary>
    public List<string> Questions { get; } = [];

    /// <summary>Every line said, in order.</summary>
    public List<string> Lines { get; } = [];

    /// <summary>All lines joined, for checks that search the output.</summary>
    public string Output => string.Join('\n', Lines);

    public void ShowMessage(string line, SetupMessageKind kind = SetupMessageKind.Info) => Lines.Add(line);

    public void ShowProgress(SetupProgress progress)
    {
    }

    public string? AskText(WizardQuestion question) => Next(question.Prompt, question.Default);

    public string? ChooseOne(WizardQuestion question)
    {
        while (true)
        {
            var text = Next(question.Prompt, question.Default);
            if (text is null)
            {
                return null;
            }

            var match = question.Options.FirstOrDefault(option => string.Equals(option, text, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }
    }

    public IReadOnlyList<string>? ChooseMany(WizardQuestion question)
    {
        var text = Next(question.Prompt, question.Default ?? string.Empty);
        if (text is null)
        {
            return null;
        }

        return text.Length == 0
            ? []
            : text.Split(',').Select(part => question.Options[int.Parse(part.Trim(), System.Globalization.CultureInfo.InvariantCulture) - 1]).ToList();
    }

    public bool? Confirm(WizardQuestion question)
    {
        while (true)
        {
            var text = Next(question.Prompt, question.Default ?? "no");
            switch (text?.ToLowerInvariant())
            {
                case null:
                    return null;
                case "y" or "yes":
                    return true;
                case "n" or "no":
                    return false;
            }
        }
    }

    public SetupSecretInput? AskSecret(WizardQuestion question)
    {
        var text = Next(question.Prompt, null);
        return text is null ? null : new SetupSecretInput(text);
    }

    /// <summary>The plan hash the review said, read from its "Plan hash:" line. The last one wins, as the apply review prints it last.</summary>
    public string? PlanHash => Lines.LastOrDefault(line => line.StartsWith("Plan hash: ", StringComparison.Ordinal))?["Plan hash: ".Length..].Trim();

    private string? Next(string question, string? defaultValue)
    {
        Questions.Add(question);
        if (_answers.Count == 0)
        {
            return null;
        }

        var answer = _answers.Dequeue();
        return answer.Length == 0 && defaultValue is not null ? defaultValue : answer;
    }
}
