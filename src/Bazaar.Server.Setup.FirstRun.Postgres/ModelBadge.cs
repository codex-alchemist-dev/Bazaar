using System.Text.RegularExpressions;

namespace Bazaar.Server.Setup.FirstRun.Postgres;

/// <summary>
/// The short model label of a seeded agent, such as <c>Haiku 5.5</c>, read from the first Claude route on its whitelist
/// (doc 45 section 2.9, doc 24 Org directory fields). A route that names no Claude model gives no label, never a guess.
/// </summary>
public static partial class ModelBadge
{
    /// <summary>The label for the first route that names a Claude model, or null when none does.</summary>
    public static string? FromRoutes(IEnumerable<string> routes)
    {
        foreach (var route in routes)
        {
            var match = ClaudeModel().Match(route);
            if (!match.Success) continue;
            var family = match.Groups["family"].Value.ToLowerInvariant();
            return $"{char.ToUpperInvariant(family[0])}{family[1..]} {match.Groups["major"].Value}.{match.Groups["minor"].Value}";
        }

        return null;
    }

    [GeneratedRegex(@"claude-(?<family>haiku|sonnet|opus)-(?<major>\d+)-(?<minor>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex ClaudeModel();
}
