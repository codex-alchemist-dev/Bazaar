using System.Globalization;

namespace Bazaar.Localisation;

/// <summary>Reading direction of a locale's script.</summary>
public enum TextDirection
{
    /// <summary>Left to right.</summary>
    LeftToRight,

    /// <summary>Right to left; layouts mirror and directional icons flip.</summary>
    RightToLeft,
}

/// <summary>
/// Doc 49 section 2: layout supports right-to-left scripts from the start. The heads read the direction from here and set
/// <c>FlowDirection</c>; this library has no UI dependency.
/// </summary>
public static class TextDirectionOf
{
    // Primary language subtags whose default script is right to left.
    private static readonly HashSet<string> _rtlLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "ar", "fa", "he", "iw", "ur", "ps", "sd", "ug", "yi", "dv", "ckb", "ks", "syr",
    };

    // Scripts that are right to left whatever the language.
    private static readonly HashSet<string> _rtlScripts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Arab", "Hebr", "Thaa", "Syrc", "Nkoo", "Adlm",
    };

    /// <summary>Returns the direction for a BCP 47 tag such as "ar-EG", "he" or "pa-Arab"; unknown or empty is left to right.</summary>
    public static TextDirection ForLocale(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return TextDirection.LeftToRight;
        }

        string[] parts = tag.Trim().Replace('_', '-').Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return TextDirection.LeftToRight;
        }

        // An explicit four-letter script subtag wins over the language default.
        foreach (string part in parts.Skip(1))
        {
            if (part.Length == 4 && char.IsLetter(part[0]))
            {
                return _rtlScripts.Contains(part) ? TextDirection.RightToLeft : TextDirection.LeftToRight;
            }
        }

        return _rtlLanguages.Contains(parts[0]) ? TextDirection.RightToLeft : TextDirection.LeftToRight;
    }

    /// <summary>Returns the direction for a culture.</summary>
    public static TextDirection ForCulture(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return culture.TextInfo.IsRightToLeft ? TextDirection.RightToLeft : ForLocale(culture.Name);
    }
}
