using System.Text.RegularExpressions;

namespace Bazaar.Client.LocalInference.Guards;

/// <summary>
/// Fixed checks on model output (doc 31, Design and Security). Every number, name and URL in the input must survive
/// in the output. Names are capitalised words of two or more letters and quoted spans. Numbers match as whole tokens,
/// so "15" does not survive as "150". The checks are not configurable.
/// </summary>
public static partial class OutputGuard
{
    /// <summary>Checks <paramref name="output"/> against <paramref name="input"/> without applying the cap.</summary>
    public static GuardReport Check(string input, string output)
    {
        var urls = Urls(input).ToList();
        var textWithoutUrls = UrlPattern().Replace(input, " ");

        var numbers = NumberPattern().Matches(textWithoutUrls).Select(m => m.Value).Distinct(StringComparer.Ordinal);
        var outputNumbers = NumberPattern().Matches(output).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        var droppedNumbers = numbers.Where(n => !outputNumbers.Contains(n)).ToList();

        var outputWords = WordPattern().Matches(output).Select(m => m.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = CapitalisedWordPattern().Matches(textWithoutUrls).Select(m => m.Value)
            .Concat(QuotedSpanPattern().Matches(textWithoutUrls).Select(m => m.Groups["span"].Value.Trim()).Where(s => s.Length > 0))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var droppedNames = names
            .Where(n => !(n.Contains(' ') ? output.Contains(n, StringComparison.OrdinalIgnoreCase) : outputWords.Contains(n)))
            .ToList();

        var droppedUrls = urls.Where(u => !output.Contains(u, StringComparison.Ordinal)).ToList();

        return new GuardReport(droppedNumbers, droppedNames, droppedUrls);
    }

    /// <summary>
    /// Caps the model output, runs the guards on the capped text, and returns the raw input when a guard fails
    /// or the output is empty. The model output is shown only when it passes.
    /// </summary>
    public static GuardedOutput Apply(LocalTaskKind kind, string input, string output)
    {
        var capped = OutputCaps.Apply(kind, output);
        var report = Check(input, capped.Text);
        if (report.Passed && !string.IsNullOrWhiteSpace(capped.Text))
        {
            return new GuardedOutput(capped.Text, capped.Truncated, FellBack: false, report);
        }

        return new GuardedOutput(input, capped.Truncated, FellBack: true, report);
    }

    private static IEnumerable<string> Urls(string input) =>
        UrlPattern().Matches(input)
            .Select(m => m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '\''))
            .Where(u => u.Length > 0)
            .Distinct(StringComparer.Ordinal);

    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>""“”]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"\d+(?:[.,]\d+)*")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"(?<!\p{L})\p{Lu}\p{L}+(?!\p{L})")]
    private static partial Regex CapitalisedWordPattern();

    [GeneratedRegex("[\"“](?<span>[^\"“”\\r\\n]+)[\"”]")]
    private static partial Regex QuotedSpanPattern();
}
