using System.Globalization;
using System.Text;

namespace Bazaar.Localisation;

/// <summary>
/// Normalises text for matching (doc 49 section 2: search and the command palette normalise Unicode to NFC and ignore
/// case and diacritics). <see cref="Fold"/> decomposes, drops combining marks, recomposes to NFC and lower-cases, so
/// "Café", "cafe" and "CAFÉ" (precomposed or with a combining accent) fold to the same string. It is for comparing
/// and never for display. Combining marks are dropped except the Japanese voiced and semi-voiced sound marks, which change the word;
/// final sigma folds to sigma.
/// </summary>
public static class TextFold
{
    /// <summary>Returns the folded form of <paramref name="text"/>; null becomes the empty string.</summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            // Keep the Japanese (semi-)voiced sound marks: they change the word (は, ば, ぱ), unlike accents.
            if (c is '\u3099' or '\u309A' || CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        // Greek final sigma lower-cases to itself, so map it to sigma to match the capital.
        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('\u03C2', '\u03C3');
    }

    /// <summary>True when the folded <paramref name="text"/> starts with the folded <paramref name="prefix"/>.</summary>
    public static bool StartsWith(string? text, string? prefix) =>
        Fold(text).StartsWith(Fold(prefix), StringComparison.Ordinal);

    /// <summary>True when the folded <paramref name="text"/> contains the folded <paramref name="part"/>.</summary>
    public static bool Contains(string? text, string? part) =>
        Fold(text).Contains(Fold(part), StringComparison.Ordinal);
}
