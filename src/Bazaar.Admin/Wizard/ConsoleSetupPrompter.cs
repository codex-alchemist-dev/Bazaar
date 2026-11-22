using System.Globalization;
using Bazaar.Server.Setup;

namespace Bazaar.Admin;

/// <summary>
/// The plain-text prompter for a terminal, or for piped input. One question per line, keyboard only, readable in order by a screen
/// reader: the question, then the default in brackets when there is one. It is the only place the wizard touches a console. Progress
/// is not drawn: a plain-text session reads in order and has no bar.
/// </summary>
public sealed class ConsoleSetupPrompter(TextReader input, TextWriter output, ISecretValueSource? secrets = null) : ISetupPrompter
{
    private const string _yesNoSuffix = " (yes/no)";

    public void ShowMessage(string line, SetupMessageKind kind = SetupMessageKind.Info)
    {
        output.WriteLine(line);
    }

    public void ShowProgress(SetupProgress progress)
    {
    }

    public string? AskText(WizardQuestion question) => Ask(question.Prompt, question.Default);

    public string? ChooseOne(WizardQuestion question)
    {
        output.WriteLine($"{question.Prompt}: {string.Join(", ", question.Options)}.");
        while (true)
        {
            var text = Ask(question.Prompt, question.Default);
            if (text is null)
            {
                return null;
            }

            var match = question.Options.FirstOrDefault(option => string.Equals(option, text, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }

            output.WriteLine("Choose one of the options listed above.");
        }
    }

    public IReadOnlyList<string>? ChooseMany(WizardQuestion question)
    {
        for (var i = 0; i < question.Options.Count; i++)
        {
            output.WriteLine($"  {i + 1}. {question.Options[i]}");
        }

        while (true)
        {
            var text = Ask(question.Prompt, question.Default ?? string.Empty);
            if (text is null)
            {
                return null;
            }

            if (TryParseChoices(text, question.Options.Count, out var indexes))
            {
                return indexes.Select(index => question.Options[index]).ToList();
            }

            output.WriteLine($"Give numbers from 1 to {question.Options.Count}, separated by commas.");
        }
    }

    public bool? Confirm(WizardQuestion question)
    {
        var fallback = string.Equals(question.Default, "yes", StringComparison.OrdinalIgnoreCase) ? "yes" : "no";
        while (true)
        {
            var text = Ask(question.Prompt + _yesNoSuffix, fallback);
            if (text is null)
            {
                return null;
            }

            switch (text.ToLowerInvariant())
            {
                case "y" or "yes":
                    return true;
                case "n" or "no":
                    return false;
                default:
                    output.WriteLine("Answer yes or no.");
                    break;
            }
        }
    }

    public SetupSecretInput? AskSecret(WizardQuestion question)
    {
        var value = secrets?.Read(question.Prompt + ": ");
        return value is null ? null : new SetupSecretInput(value);
    }

    private string? Ask(string question, string? defaultValue)
    {
        output.Write(defaultValue is null ? $"{question}: " : $"{question} [{defaultValue}]: ");
        output.Flush();

        var line = input.ReadLine();
        if (line is null)
        {
            return null;
        }

        var answer = line.Trim();
        return answer.Length == 0 && defaultValue is not null ? defaultValue : answer;
    }

    private static bool TryParseChoices(string text, int count, out List<int> indexes)
    {
        indexes = [];
        if (text.Length == 0)
        {
            return true;
        }

        foreach (var part in text.Split(','))
        {
            if (!int.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1 || number > count)
            {
                return false;
            }

            if (!indexes.Contains(number - 1))
            {
                indexes.Add(number - 1);
            }
        }

        return true;
    }
}
