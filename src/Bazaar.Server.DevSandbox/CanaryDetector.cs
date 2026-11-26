using System.Text;
using System.Text.RegularExpressions;

namespace Bazaar.Server.DevSandbox;

/// <summary>
/// Finds canary values (doc 48 section 2.1 rule 3) in text that leaves a sandbox run. Besides the raw text it checks: the decoded
/// form when the whole text is base64, the URL-decoded form (up to two rounds), the decoded form when the whole text is hex, and
/// the base64 (standard and URL-safe, at all three byte alignments) and hex (either case) encodings of each canary embedded anywhere
/// in a longer text. Raw matching is ordinal and case-sensitive. Not detected: UTF-16 or reversed text, compression, XOR or any
/// encryption, and a canary split across two separate calls (the runner joins consecutive payloads for the raw and decoded views).
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

            if (ContainsEncoded(views, canary))
            {
                return canary;
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
            var twice = TryUrlDecode(unescaped);
            if (twice is not null)
            {
                views.Add(twice);
            }
        }

        var hex = TryDecodeHex(text);
        if (hex is not null)
        {
            views.Add(hex);
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

    private static string? TryDecodeHex(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length < 2 || trimmed.Length % 2 != 0)
        {
            return null;
        }

        foreach (var c in trimmed)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return null;
            }
        }

        return Encoding.UTF8.GetString(Convert.FromHexString(trimmed));
    }

    /// <summary>Looks for the hex or base64 encoding of the canary inside any view, wherever it sits in a longer text.</summary>
    private static bool ContainsEncoded(List<string> views, string canary)
    {
        var bytes = Encoding.UTF8.GetBytes(canary);
        var hex = Convert.ToHexString(bytes);
        var b64 = Base64Fragments(bytes);
        foreach (var view in views)
        {
            if (view.Contains(hex, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (var fragment in b64)
            {
                if (fragment.Length > 0 && view.Contains(fragment, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The base64 substrings fully determined by the canary bytes at each of the three alignments a longer base64 stream can place
    /// them at, in standard and URL-safe alphabets. Characters that also depend on neighbouring bytes are left out.
    /// </summary>
    private static IEnumerable<string> Base64Fragments(byte[] bytes)
    {
        for (var offset = 0; offset < 3; offset++)
        {
            var padded = new byte[offset + bytes.Length];
            bytes.CopyTo(padded, offset);
            var encoded = Convert.ToBase64String(padded);
            var start = (offset * 8 + 5) / 6;
            var end = padded.Length * 8 / 6;
            if (end <= start)
            {
                continue;
            }

            var fragment = encoded[start..end];
            yield return fragment;
            yield return fragment.Replace('+', '-').Replace('/', '_');
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
