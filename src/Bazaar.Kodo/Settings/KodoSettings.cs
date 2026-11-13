namespace Bazaar.Kodo.Settings;

/// <summary>
/// A complete set of Kodo settings: canonical text for every catalogue key. Build it with
/// <see cref="KodoSettingsValidator"/> or take <see cref="Defaults"/>; the typed accessors parse on read.
/// </summary>
public sealed record KodoSettings(IReadOnlyDictionary<string, string> Values)
{
    /// <summary>The shipped defaults for every catalogue key.</summary>
    public static KodoSettings Defaults { get; } =
        new(KodoSettingKey.All.ToDictionary(definition => definition.Key, definition => definition.Default, StringComparer.Ordinal));

    public int GetInt(string key) => (int)Read(key, KodoSettingType.WholeNumber)[0];

    public decimal GetDecimal(string key) => Read(key, KodoSettingType.Number)[0];

    public IReadOnlyList<int> GetIntList(string key) => Read(key, KodoSettingType.IntegerList).Select(value => (int)value).ToArray();

    private IReadOnlyList<decimal> Read(string key, KodoSettingType expected)
    {
        var definition = KodoSettingKey.Find(key) ?? throw new ArgumentException($"Unknown kodo setting '{key}'.", nameof(key));
        if (definition.Type != expected)
        {
            throw new InvalidOperationException($"kodo setting '{key}' is a {definition.Type}, not a {expected}.");
        }

        // A missing key reads as its default; a stored value that does not parse is a bug in the caller.
        var raw = Values.TryGetValue(key, out var stored) ? stored : definition.Default;
        if (!KodoSettingParser.TryParse(definition.Type, raw, out var values))
        {
            throw new InvalidOperationException($"kodo setting '{key}' holds an unparseable value.");
        }

        return values;
    }
}
