using System.Globalization;

namespace Bazaar.Client.Tui;

/// <summary>
/// The console implementation of <see cref="IPromptWidgets"/> over an <see cref="ITerminal"/>. On a terminal that reads keys, choices
/// are arrow-key lists (Up and Down move, Space toggles in a multi-choice, Enter accepts). Otherwise (a pipe, a script, a test) every
/// widget reads one line, in the same order and wording as the plain wizard. A secret is read key by key without echo, or as one
/// unechoed line when keys are not supported; it is never written to the terminal.
/// </summary>
public sealed class TerminalPromptWidgets(ITerminal terminal) : IPromptWidgets
{
    public string? TextEntry(string prompt, string? defaultValue)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return Ask(prompt, defaultValue);
    }

    public string? SecretEntry(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        terminal.Write(prompt + ": ");

        if (!terminal.SupportsKeys)
        {
            // Piped input is not echoed by this port, and the line is not written back.
            var line = terminal.ReadLine();
            terminal.WriteLine();
            return line;
        }

        var typed = new List<char>();
        try
        {
            while (true)
            {
                var key = terminal.ReadKey();
                switch (key.Kind)
                {
                    case TerminalKeyKind.Cancel or TerminalKeyKind.EndOfInput:
                        terminal.WriteLine();
                        return null;
                    case TerminalKeyKind.Enter:
                        terminal.WriteLine();
                        return new string(typed.ToArray());
                    case TerminalKeyKind.Backspace:
                        if (typed.Count > 0)
                        {
                            typed.RemoveAt(typed.Count - 1);
                        }

                        break;
                    case TerminalKeyKind.Character when !char.IsControl(key.Value):
                        typed.Add(key.Value);
                        break;
                }
            }
        }
        finally
        {
            // Nothing typed stays in this widget after it returns.
            typed.Clear();
            typed.TrimExcess();
        }
    }

    public string? PickOne(string prompt, IReadOnlyList<string> options, string? defaultOption)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(options);
        return terminal.SupportsKeys ? PickOneWithKeys(prompt, options, defaultOption) : PickOneByLine(prompt, options, defaultOption);
    }

    public IReadOnlyList<string>? PickMany(string prompt, IReadOnlyList<string> options, IReadOnlyList<string> defaults)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(defaults);
        return terminal.SupportsKeys ? PickManyWithKeys(prompt, options, defaults) : PickManyByLine(prompt, options, defaults);
    }

    public bool? YesNo(string prompt, bool defaultYes)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        while (true)
        {
            var text = Ask(prompt + " (yes/no)", defaultYes ? "yes" : "no");
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
                    terminal.WriteLine("Answer yes or no.");
                    break;
            }
        }
    }

    private string? Ask(string prompt, string? defaultValue)
    {
        terminal.Write(string.IsNullOrEmpty(defaultValue) ? $"{prompt}: " : $"{prompt} [{defaultValue}]: ");
        var line = terminal.ReadLine();
        if (line is null)
        {
            return null;
        }

        var answer = line.Trim();
        return answer.Length == 0 ? defaultValue ?? string.Empty : answer;
    }

    private string? PickOneByLine(string prompt, IReadOnlyList<string> options, string? defaultOption)
    {
        for (var index = 0; index < options.Count; index++)
        {
            terminal.WriteLine($"  {index + 1}. {options[index]}");
        }

        while (true)
        {
            var text = Ask(prompt, defaultOption);
            if (text is null)
            {
                return null;
            }

            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1 && number <= options.Count)
            {
                return options[number - 1];
            }

            var match = options.FirstOrDefault(option => string.Equals(option, text, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }

            terminal.WriteLine("Choose one of the options listed above.");
        }
    }

    private List<string>? PickManyByLine(string prompt, IReadOnlyList<string> options, IReadOnlyList<string> defaults)
    {
        for (var index = 0; index < options.Count; index++)
        {
            terminal.WriteLine($"  {index + 1}. {options[index]}");
        }

        var defaultText = string.Join(",", defaults.Select(value => options.ToList().IndexOf(value) + 1).Where(number => number > 0));
        while (true)
        {
            var text = Ask(prompt, defaultText);
            if (text is null)
            {
                return null;
            }

            if (TryParseNumbers(text, options.Count, out var indexes))
            {
                return indexes.Select(index => options[index]).ToList();
            }

            terminal.WriteLine($"Give numbers from 1 to {options.Count}, separated by commas.");
        }
    }

    private string? PickOneWithKeys(string prompt, IReadOnlyList<string> options, string? defaultOption)
    {
        if (options.Count == 0)
        {
            return null;
        }

        var cursor = Math.Max(0, ListIndex(options, defaultOption));
        terminal.WriteLine(prompt + " (Up and Down move, Enter chooses)");
        while (true)
        {
            DrawList(options, cursor, null);
            var key = terminal.ReadKey();
            switch (key.Kind)
            {
                case TerminalKeyKind.Cancel or TerminalKeyKind.EndOfInput:
                    return null;
                case TerminalKeyKind.Enter:
                    return options[cursor];
                case TerminalKeyKind.Up:
                    cursor = (cursor - 1 + options.Count) % options.Count;
                    break;
                case TerminalKeyKind.Down:
                    cursor = (cursor + 1) % options.Count;
                    break;
            }

            terminal.EraseLinesAbove(options.Count);
        }
    }

    private List<string>? PickManyWithKeys(string prompt, IReadOnlyList<string> options, IReadOnlyList<string> defaults)
    {
        var chosen = options.Select(option => defaults.Contains(option, StringComparer.Ordinal)).ToArray();
        var cursor = 0;
        terminal.WriteLine(prompt + " (Up and Down move, Space ticks, Enter accepts)");
        while (options.Count > 0)
        {
            DrawList(options, cursor, chosen);
            var key = terminal.ReadKey();
            switch (key.Kind)
            {
                case TerminalKeyKind.Cancel or TerminalKeyKind.EndOfInput:
                    return null;
                case TerminalKeyKind.Enter:
                    return options.Where((_, index) => chosen[index]).ToList();
                case TerminalKeyKind.Up:
                    cursor = (cursor - 1 + options.Count) % options.Count;
                    break;
                case TerminalKeyKind.Down:
                    cursor = (cursor + 1) % options.Count;
                    break;
                case TerminalKeyKind.Character when key.Value == ' ':
                    chosen[cursor] = !chosen[cursor];
                    break;
            }

            terminal.EraseLinesAbove(options.Count);
        }

        return [];
    }

    /// <summary>The pointer and the tick are text, not colour: "&gt;" marks the cursor and "[x]" a ticked entry.</summary>
    private void DrawList(IReadOnlyList<string> options, int cursor, bool[]? chosen)
    {
        for (var index = 0; index < options.Count; index++)
        {
            var pointer = index == cursor ? ">" : " ";
            var tick = chosen is null ? string.Empty : chosen[index] ? "[x] " : "[ ] ";
            terminal.WriteLine($" {pointer} {tick}{options[index]}");
        }
    }

    private static int ListIndex(IReadOnlyList<string> options, string? option)
    {
        for (var index = 0; index < options.Count; index++)
        {
            if (string.Equals(options[index], option, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool TryParseNumbers(string text, int count, out List<int> indexes)
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

        indexes.Sort();
        return true;
    }
}
