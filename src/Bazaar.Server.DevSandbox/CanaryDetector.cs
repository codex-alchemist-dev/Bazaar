using System.Text;
using System.Text.RegularExpressions;

namespace Bazaar.Server.DevSandbox;

/// <summary>
/// Finds canary values (doc 48 section 2.1 rule 3) in text that leaves a sandbox run. Besides the raw text it checks two trivial
/// encodings of the text: the decoded form when the text is base64, and the URL-decoded form. Matching is ordinal and case-sensitive.
/// </summary>
public static partial class CanaryDetector
{
    /// <summary>Returns the first canary found in <paramref name="text"/>, or null when none is found.</summary>
    /// <param name="text">The text to scan, such as a request body or a written file's content.</param>
    /// <param name="canaries">The canary values planted in the test company. Empty entries are skipped.</param>
    /// <returns>The matching canary, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> or <paramref name="canaries"/> is null.</exception>
    public static string? Find(string text, IReadOnlyList<string> canaries)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(canaries);

        if (text.Length == 0 || canaries.Count == 0)
        {
            return null;
        }

        var views = BuildViews(text);

        foreach (var canary in canaries)
        {
            if (string.IsNullOrEmpty(canary))
            {
                continue;
            }

            foreach (var view in views)
            {
                if (view.Contains(canary, StringComparison.Ordinal))
                {
                    return canary;
                }
            }
        }

        return null;
    }

    private static List<string> BuildViews(string text)
    {
        var views = new List<string> { text };

        var decoded = TryDecodeBase64(text);
        if (decoded is not null)
        {
            views.Add(decoded);
        }

        var unescaped = TryUrlDecode(text);
        if (unescaped is not null)
        {
            views.Add(unescaped);
        }

        return views;
    }

    [GeneratedRegex("^[A-Za-z0-9+/]+={0,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex Base64Shape();

    private static string? TryDecodeBase64(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length < 4 || trimmed.Length % 4 != 0 || !Base64Shape().IsMatch(trimmed))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(trimmed));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? TryUrlDecode(string text)
    {
        try
        {
            var unescaped = Uri.UnescapeDataString(text);
            return string.Equals(unescaped, text, StringComparison.Ordinal) ? null : unescaped;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }
}
