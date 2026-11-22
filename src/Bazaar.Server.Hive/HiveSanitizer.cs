using System.Text;
using Bazaar.Server.Memory.Notes;

namespace Bazaar.Server.Hive;

/// <summary>Names the sanitizer must keep out of a copy. The origin project is always checked.</summary>
/// <param name="OriginProject">The project the lesson came from.</param>
/// <param name="OtherProjects">Other project names or slugs.</param>
/// <param name="Customers">Customer names.</param>
/// <param name="People">Person names or handles.</param>
public sealed record HiveSanitizerTerms(
    string OriginProject,
    IReadOnlyList<string> OtherProjects,
    IReadOnlyList<string> Customers,
    IReadOnlyList<string> People)
{
    /// <summary>Terms with only the origin project.</summary>
    public static HiveSanitizerTerms ForOrigin(string originProject) => new(originProject, [], [], []);
}

/// <summary>Why the sanitizer rejected a text.</summary>
public enum HiveSanitizerFinding
{
    /// <summary>The text names the origin project.</summary>
    OriginProject,

    /// <summary>The text names another project.</summary>
    Project,

    /// <summary>The text names a customer.</summary>
    Customer,

    /// <summary>The text names a person.</summary>
    Person,

    /// <summary>The text contains a credential-shaped token.</summary>
    Secret,
}

/// <summary>Sanitizer verdict. Findings are codes only; the matched text is never returned.</summary>
/// <param name="Findings">Distinct findings, in enum order.</param>
public sealed record HiveSanitizerResult(IReadOnlyList<HiveSanitizerFinding> Findings)
{
    /// <summary>True when nothing was found.</summary>
    public bool IsClean => Findings.Count == 0;
}

/// <summary>
/// Pure pass before a Hive copy (doc 08, section 6; doc 25): rejects text that names the origin project, a
/// customer, a person or another project, or that holds a secret pattern. Matching ignores case and treats
/// <c>-</c> and <c>_</c> as spaces, and only matches whole words so a short name inside another word does not trip it.
/// </summary>
public static class HiveSanitizer
{
    /// <summary>Checks a title and body.</summary>
    public static HiveSanitizerResult Check(string title, string body, HiveSanitizerTerms terms)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(terms);

        var findings = new List<HiveSanitizerFinding>();
        var text = Normalize(title + "\n" + body);

        if (Names(text, [terms.OriginProject]))
        {
            findings.Add(HiveSanitizerFinding.OriginProject);
        }

        if (Names(text, terms.OtherProjects))
        {
            findings.Add(HiveSanitizerFinding.Project);
        }

        if (Names(text, terms.Customers))
        {
            findings.Add(HiveSanitizerFinding.Customer);
        }

        if (Names(text, terms.People))
        {
            findings.Add(HiveSanitizerFinding.Person);
        }

        if (SecretScanner.ContainsSecret(title) || SecretScanner.ContainsSecret(body))
        {
            findings.Add(HiveSanitizerFinding.Secret);
        }

        return new HiveSanitizerResult(findings);
    }

    private static bool Names(string normalizedText, IReadOnlyList<string> terms)
    {
        foreach (var raw in terms)
        {
            var term = Normalize(raw);
            if (term.Length == 0)
            {
                continue;
            }

            var from = 0;
            while (from <= normalizedText.Length - term.Length)
            {
                var at = normalizedText.IndexOf(term, from, StringComparison.Ordinal);
                if (at < 0)
                {
                    break;
                }

                var before = at == 0 || !char.IsLetterOrDigit(normalizedText[at - 1]);
                var end = at + term.Length;
                var after = end == normalizedText.Length || !char.IsLetterOrDigit(normalizedText[end]);
                if (before && after)
                {
                    return true;
                }

                from = at + 1;
            }
        }

        return false;
    }

    /// <summary>Lower-cases, maps <c>-</c> and <c>_</c> to spaces and collapses white space.</summary>
    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        var lastSpace = true;
        foreach (var c in text)
        {
            var ch = c is '-' or '_' || char.IsWhiteSpace(c) ? ' ' : char.ToLowerInvariant(c);
            if (ch == ' ')
            {
                if (!lastSpace)
                {
                    sb.Append(' ');
                }

                lastSpace = true;
            }
            else
            {
                sb.Append(ch);
                lastSpace = false;
            }
        }

        return sb.ToString().Trim();
    }
}
