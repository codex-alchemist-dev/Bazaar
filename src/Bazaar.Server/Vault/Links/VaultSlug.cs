using System.Text;

namespace Bazaar.Server.Vault.Links;

/// <summary>Heading slugs for <c>[[Title#Heading]]</c> anchor matching. Owned by VLT2.</summary>
public static class VaultSlug
{
    /// <summary>
    /// Slugs a heading: trimmed, lowercased (invariant), Unicode letters and digits, <c>-</c> and <c>_</c> kept,
    /// each whitespace run becomes one <c>-</c>, and every other character is dropped.
    /// Null or whitespace input gives an empty string.
    /// </summary>
    public static string Heading(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lowered = text.Trim().ToLowerInvariant();
        var builder = new StringBuilder(lowered.Length);
        var pendingSeparator = false;

        foreach (var rune in lowered.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                // A whitespace run only emits a separator once a kept character follows it, so leading and trailing runs vanish.
                pendingSeparator = builder.Length > 0;
            }
            else if (Rune.IsLetterOrDigit(rune) || rune.Value == '-' || rune.Value == '_')
            {
                if (pendingSeparator)
                {
                    builder.Append('-');
                    pendingSeparator = false;
                }

                builder.Append(rune);
            }
            // Any other character is dropped and does not end a whitespace run.
        }

        return builder.ToString();
    }
}
