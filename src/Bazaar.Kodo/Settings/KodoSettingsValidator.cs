namespace Bazaar.Kodo.Settings;

/// <summary>A problem with one operator-supplied setting.</summary>
/// <param name="Key">The setting key the problem is about.</param>
/// <param name="Message">A plain sentence on what is wrong.</param>
public sealed record KodoSettingError(string Key, string Message);

/// <summary>
/// The outcome of validating operator values. <see cref="Settings"/> holds the defaults with every
/// valid override applied; invalid overrides are left at their defaults and listed in <see cref="Errors"/>.
/// Callers must refuse to start Kodo with the new values while <see cref="IsValid"/> is false.
/// </summary>
public sealed record KodoSettingsResult(KodoSettings Settings, IReadOnlyList<KodoSettingError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Checks operator values against the catalogue: known key, parseable type, inside the range and not
/// looser than the floor. Settings may tighten, never loosen below the floor (doc 46 section 20).
/// Pure; no I/O.
/// </summary>
public static class KodoSettingsValidator
{
    public static KodoSettingsResult Validate(IReadOnlyDictionary<string, string> operatorValues)
    {
        ArgumentNullException.ThrowIfNull(operatorValues);

        var errors = new List<KodoSettingError>();
        var merged = new Dictionary<string, string>(KodoSettings.Defaults.Values, StringComparer.Ordinal);

        // Sorted so the same input always reports errors in the same order.
        foreach (var (key, raw) in operatorValues.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var definition = KodoSettingKey.Find(key);
            if (definition is null)
            {
                errors.Add(new KodoSettingError(key, "Unknown kodo setting."));
                continue;
            }

            if (!KodoSettingParser.TryParse(definition.Type, raw, out var values))
            {
                errors.Add(new KodoSettingError(key, $"Value is not a valid {definition.Type}."));
                continue;
            }

            var problem = CheckRange(definition, values) ?? CheckFloor(definition, values);
            if (problem is not null)
            {
                errors.Add(new KodoSettingError(key, problem));
                continue;
            }

            merged[key] = KodoSettingParser.Format(definition.Type, values);
        }

        return new KodoSettingsResult(new KodoSettings(merged), errors);
    }

    private static string? CheckRange(KodoSettingDefinition definition, IReadOnlyList<decimal> values)
    {
        foreach (var value in values)
        {
            if (value < definition.Min || value > definition.Max)
            {
                return $"{value} is outside the allowed range {definition.Min} to {definition.Max}.";
            }
        }

        return null;
    }

    private static string? CheckFloor(KodoSettingDefinition definition, IReadOnlyList<decimal> values)
    {
        foreach (var value in values)
        {
            var loosens = definition.FloorRule == KodoFloorRule.AtLeast
                ? value < definition.Floor
                : value > definition.Floor;
            if (loosens)
            {
                return $"{value} loosens the setting past its floor of {definition.Floor}. Settings may tighten, never loosen.";
            }
        }

        return null;
    }
}
