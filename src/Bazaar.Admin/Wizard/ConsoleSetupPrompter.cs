namespace Bazaar.Admin;

/// <summary>
/// The plain-text prompter for a terminal, or for piped input. One question per line, keyboard only, readable in order by a screen
/// reader: the question, then the default in brackets when there is one.
/// </summary>
public sealed class ConsoleSetupPrompter(TextReader input, TextWriter output) : ISetupPrompter
{
    public void Say(string line)
    {
        output.WriteLine(line);
    }

    public string? Ask(string question, string? defaultValue)
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
}
