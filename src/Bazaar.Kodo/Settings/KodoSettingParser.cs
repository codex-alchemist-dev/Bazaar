using System.Globalization;

namespace Bazaar.Kodo.Settings;

/// <summary>Strict parsing and canonical formatting of setting values. Pure; no I/O.</summary>
internal static class KodoSettingParser
{
    /// <summary>Parses a raw value into its numbers. A scalar is a one-element list.</summary>
    public static bool TryParse(KodoSettingType type, string? raw, out IReadOnlyList<decimal> values)
    {
        values = [];
        if (raw is null)
        {
            return false;
        }

        var text = raw.Trim();
        switch (type)
        {
            case KodoSettingType.WholeNumber:
                if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
                {
                    values = [whole];
                    return true;
                }

                return false;

            case KodoSettingType.Number:
                if (decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out var number))
                {
                    values = [number];
                    return true;
                }

                return false;

            case KodoSettingType.IntegerList:
                var parts = text.Split(',');
                var list = new List<decimal>(parts.Length);
                foreach (var part in parts)
                {
                    if (!int.TryParse(part.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var item))
                    {
                        return false;
                    }

                    list.Add(item);
                }

                values = list;
                return true;

            default:
                return false;
        }
    }

    /// <summary>Formats parsed values back to the canonical text stored in settings.</summary>
    public static string Format(KodoSettingType type, IReadOnlyList<decimal> values) => type switch
    {
        KodoSettingType.WholeNumber => ((int)values[0]).ToString(CultureInfo.InvariantCulture),
        KodoSettingType.Number => values[0].ToString(CultureInfo.InvariantCulture),
        KodoSettingType.IntegerList => string.Join(',', values.Select(value => ((int)value).ToString(CultureInfo.InvariantCulture))),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown setting type."),
    };
}
