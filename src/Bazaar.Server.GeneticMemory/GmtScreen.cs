using System.Text;
using Bazaar.Server.GeneticMemory.Abstractions;

namespace Bazaar.Server.GeneticMemory;

/// <summary>What the screen found. Codes only.</summary>
public enum GmtFinding
{
    /// <summary>A credential-shaped token.</summary>
    Secret,

    /// <summary>A person named in <see cref="GmtTerms.People"/>.</summary>
    Person,

    /// <summary>A customer named in <see cref="GmtTerms.Customers"/>.</summary>
    Customer,

    /// <summary>A project named in <see cref="GmtTerms.Projects"/>.</summary>
    Project,
}

/// <summary>
/// Pure pass before staging and again before accepting an edit: a candidate must hold no secret and name no person,
/// customer or project from the caller's terms. Matching ignores case, treats <c>-</c> and <c>_</c> as spaces and
/// matches whole words only.
/// </summary>
public static class GmtScreen
{
    /// <summary>Checks a title and body. Findings are distinct and in enum order.</summary>
    public static IReadOnlyList<GmtFinding> Check(string title, string body, GmtTerms terms, IGmtSecretScanner secrets)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(secrets);

        var findings = new List<GmtFinding>();
        if (secrets.ContainsSecret(title) || secrets.ContainsSecret(body))
        {
            findings.Add(GmtFinding.Secret);
        }

        var text = " " + Normalise(title + "\n" + body) + " ";
        if (Names(text, terms.People))
        {
            findings.Add(GmtFinding.Person);
        }

        if (Names(text, terms.Customers))
        {
            findings.Add(GmtFinding.Customer);
        }

        if (Names(text, terms.Projects))
        {
            findings.Add(GmtFinding.Project);
        }

        return findings;
    }

    private static bool Names(string text, IReadOnlyList<string> terms)
    {
        foreach (var term in terms)
        {
            var needle = Normalise(term).Trim();
            if (needle.Length > 0 && text.Contains(" " + needle + " ", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string Normalise(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        // Collapse runs so "a  -  b" and "a b" match the same way.
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
