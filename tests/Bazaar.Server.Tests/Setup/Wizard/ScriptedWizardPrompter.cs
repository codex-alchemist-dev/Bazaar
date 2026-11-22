using Bazaar.Server.Setup;

namespace Bazaar.Server.Tests.Setup.Wizard;

/// <summary>
/// A prompter that answers from a list, one answer per question, in order. An empty answer takes the default. It records every
/// question id, every line shown and every progress report. When the answers run out the input has ended.
/// </summary>
internal sealed class ScriptedWizardPrompter(IEnumerable<string> answers) : ISetupPrompter
{
    private readonly Queue<string> _answers = new(answers);

    public List<string> AskedIds { get; } = [];

    public List<string> Prompts { get; } = [];

    public List<string> Lines { get; } = [];

    public List<SetupProgress> Progress { get; } = [];

    public void ShowMessage(string line, SetupMessageKind kind = SetupMessageKind.Info) => Lines.Add(line);

    public void ShowProgress(SetupProgress progress) => Progress.Add(progress);

    public string? AskText(WizardQuestion question) => Next(question);

    public string? ChooseOne(WizardQuestion question)
    {
        while (true)
        {
            var text = Next(question);
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
        var text = Next(question);
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
        var text = Next(question);
        return text is null ? null : text.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    public SetupSecretInput? AskSecret(WizardQuestion question)
    {
        var text = Next(question);
        return text is null ? null : new SetupSecretInput(text);
    }

    private string? Next(WizardQuestion question)
    {
        AskedIds.Add(question.Id);
        Prompts.Add(question.Prompt);
        if (_answers.Count == 0)
        {
            return null;
        }

        var answer = _answers.Dequeue();
        return answer.Length == 0 && question.Default is not null ? question.Default : answer;
    }
}
