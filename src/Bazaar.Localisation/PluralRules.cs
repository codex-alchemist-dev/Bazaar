namespace Bazaar.Localisation;

/// <summary>The CLDR plural categories. A language uses a subset of them.</summary>
public enum PluralCategory
{
    Zero,
    One,
    Two,
    Few,
    Many,
    Other,
}

/// <summary>
/// CLDR cardinal plural rules for whole numbers, selected by the language part of a BCP 47 tag (doc 49 section 2:
/// plurals follow the ICU message rules). Languages that are not listed use the English rule, which is the safe
/// default: a wrong "one" or "other" is a readable sentence, never a crash. Non-integers are always <c>other</c>.
/// </summary>
public static class PluralRules
{
    private static readonly HashSet<string> _noPlural = new(StringComparer.OrdinalIgnoreCase)
    {
        "ja", "zh", "ko", "vi", "th", "id", "ms", "lo", "my", "km",
    };

    /// <summary>Picks the category for <paramref name="n"/> in <paramref name="locale"/> (for example <c>pt-BR</c>).</summary>
    public static PluralCategory Select(string locale, decimal n)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);

        if (n != decimal.Truncate(n))
        {
            return PluralCategory.Other;
        }

        decimal abs = Math.Abs(n);
        decimal mod10 = abs % 10;
        decimal mod100 = abs % 100;
        string language = locale.Split('-', '_')[0].ToLowerInvariant();

        if (_noPlural.Contains(language))
        {
            return PluralCategory.Other;
        }

        switch (language)
        {
            case "fr":
                return abs == 0 || abs == 1 ? PluralCategory.One : ManyOrOther(abs);
            case "pt":
                // pt-PT: "one" is only 1 (CLDR); pt and pt-BR treat 0 and 1 as "one".
                bool portugal = locale.Split('-', '_').Skip(1).Any(part => string.Equals(part, "PT", StringComparison.OrdinalIgnoreCase));
                return (portugal ? abs == 1 : abs is 0 or 1) ? PluralCategory.One : ManyOrOther(abs);
            case "es":
            case "it":
            case "ca":
                return abs == 1 ? PluralCategory.One : ManyOrOther(abs);
            case "ru":
            case "uk":
            case "be":
                if (mod10 == 1 && mod100 != 11)
                {
                    return PluralCategory.One;
                }

                return mod10 is >= 2 and <= 4 && mod100 is not (>= 12 and <= 14) ? PluralCategory.Few : PluralCategory.Many;
            case "pl":
                if (abs == 1)
                {
                    return PluralCategory.One;
                }

                return mod10 is >= 2 and <= 4 && mod100 is not (>= 12 and <= 14) ? PluralCategory.Few : PluralCategory.Many;
            case "cs":
            case "sk":
                if (abs == 1)
                {
                    return PluralCategory.One;
                }

                return abs is >= 2 and <= 4 ? PluralCategory.Few : PluralCategory.Other;
            case "ar":
                if (abs == 0)
                {
                    return PluralCategory.Zero;
                }

                if (abs == 1)
                {
                    return PluralCategory.One;
                }

                if (abs == 2)
                {
                    return PluralCategory.Two;
                }

                if (mod100 is >= 3 and <= 10)
                {
                    return PluralCategory.Few;
                }

                return mod100 is >= 11 and <= 99 ? PluralCategory.Many : PluralCategory.Other;
            default:
                return abs == 1 ? PluralCategory.One : PluralCategory.Other;
        }
    }

    /// <summary>CLDR 42: fr, es, it, pt and ca use "many" for non-zero whole multiples of 1,000,000.</summary>
    private static PluralCategory ManyOrOther(decimal abs) =>
        abs != 0 && abs % 1_000_000 == 0 ? PluralCategory.Many : PluralCategory.Other;
}
