using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bazaar.Testing.Replay;

/// <summary>
/// Rejects a fixture that still carries something that looks like personal or secret data: an email address, or a
/// string beginning with a key-like prefix (<c>sk-</c>, <c>ghp_</c>, <c>xox</c>). Every string value and every property
/// name is checked, so an unknown field cannot hide one. Messages name the JSON path only and never echo the value.
/// </summary>
public static partial class ReplaySanitizer
{
    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    // The prefix must start a token: "task-list" and "risk-report" pass, "sk-abc" and "xoxb-abc" do not.
    [GeneratedRegex(@"(?<![A-Za-z0-9_-])(?:sk-|ghp_|xox)[A-Za-z0-9_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex KeyLikePattern();

    /// <summary>Throws <see cref="ReplayFixtureException"/> listing every offending path, or returns when clean.</summary>
    public static void EnsureClean(JsonElement root, string source)
    {
        var problems = new List<string>();
        Walk(root, "$", problems);
        if (problems.Count > 0)
        {
            throw new ReplayFixtureException(source, string.Join("; ", problems));
        }
    }

    private static void Walk(JsonElement element, string path, List<string> problems)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                Check(element.GetString() ?? string.Empty, path, problems);
                break;

            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Check(property.Name, $"{path} (property name)", problems);
                    Walk(property.Value, $"{path}.{property.Name}", problems);
                }

                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, $"{path}[{index++}]", problems);
                }

                break;
        }
    }

    private static void Check(string text, string path, List<string> problems)
    {
        if (EmailPattern().IsMatch(text))
        {
            problems.Add($"{path} matches an email address pattern");
        }

        if (KeyLikePattern().IsMatch(text))
        {
            problems.Add($"{path} matches a key-like prefix (sk-, ghp_, xox)");
        }
    }
}
