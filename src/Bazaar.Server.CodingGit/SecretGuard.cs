using System.Text.RegularExpressions;

namespace Bazaar.Server.CodingGit;

/// <summary>
/// Cheap shape check for credentials in text that is about to become a commit message or a pull request. It fails
/// closed: text that looks like a secret is rejected, not redacted. The real secret scanner is a separate component.
/// </summary>
public static partial class SecretGuard
{
    /// <summary>True when the text contains something shaped like a token, private key or URL credential.</summary>
    public static bool LooksLikeSecret(string? text)
    {
        return !string.IsNullOrEmpty(text) && Pattern().IsMatch(text);
    }

    /// <summary>Throws <see cref="CodingGitError.SecretDetected"/> when the text looks like it carries a secret.</summary>
    public static void ThrowIfSecret(string? text)
    {
        if (LooksLikeSecret(text))
        {
            throw new CodingGitException(CodingGitError.SecretDetected, "The text looks like it contains a credential and was refused.");
        }
    }

    [GeneratedRegex(
        @"gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|\bsk-[A-Za-z0-9_-]{20,}|AKIA[0-9A-Z]{16}|-----BEGIN [A-Z ]*PRIVATE KEY-----|\bbearer\s+[A-Za-z0-9._~+/=-]{16,}|\bx-access-token\b|https?://[^/\s:@]+:[^/\s@]+@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
