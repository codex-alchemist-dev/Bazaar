namespace Bazaar.Client.SignIn;

/// <summary>
/// Reads a handoff code from what the person or the operating system gave the client: the bare code, or the
/// <c>bazaar://signin?code=...</c> address the sign-in page redirects to (doc 32 section 4).
/// </summary>
public static class HandoffCode
{
    private const int _maxLength = 256;

    /// <summary>Returns the code, or null when <paramref name="input"/> holds none. Never throws.</summary>
    public static string? TryRead(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var text = input.Trim();

        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Scheme, "bazaar", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(uri.Host, "signin", StringComparison.OrdinalIgnoreCase))
                return null;

            text = QueryValue(uri.Query, "code") ?? "";
        }

        return IsWellFormed(text) ? text : null;
    }

    private static bool IsWellFormed(string text)
    {
        if (text.Length is 0 or > _maxLength) return false;
        foreach (var c in text)
        {
            // The server mints URL-safe codes; anything else is a paste mistake.
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '=')) return false;
        }

        return true;
    }

    private static string? QueryValue(string query, string key)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = pair.IndexOf('=', StringComparison.Ordinal);
            if (at > 0 && string.Equals(pair[..at], key, StringComparison.Ordinal))
                return Uri.UnescapeDataString(pair[(at + 1)..]);
        }

        return null;
    }
}
