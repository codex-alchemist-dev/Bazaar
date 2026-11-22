using Bazaar.Admin;

namespace Bazaar.Admin.Tests;

/// <summary>
/// A prompter that answers from a list, in order. An empty answer takes the default, as the console does. It records every question
/// and every line the wizard says. When the answers run out, the input has ended, which stops the wizard.
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

    public void Say(string line) => Lines.Add(line);

    public string? Ask(string question, string? defaultValue)
    {
        Questions.Add(question);
        if (_answers.Count == 0)
        {
            return null;
        }

        var answer = _answers.Dequeue();
        return answer.Length == 0 && defaultValue is not null ? defaultValue : answer;
    }

    /// <summary>The plan hash the review said, read from its "Plan hash:" line. The last one wins, as the apply review prints it last.</summary>
    public string? PlanHash => Lines.LastOrDefault(line => line.StartsWith("Plan hash: ", StringComparison.Ordinal))?["Plan hash: ".Length..].Trim();
}
