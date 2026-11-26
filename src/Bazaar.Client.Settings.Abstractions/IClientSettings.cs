namespace Bazaar.Client.Settings;

/// <summary>
/// The person-scope settings a client head keeps on its own device (doc 93, Settings: <c>client.theme</c>, <c>client.density</c>,
/// <c>client.materials</c>, <c>client.motion_level</c>). Values are strings in their wire form; the module that owns a key parses
/// them. Keys must be registered with a <see cref="ClientSettingDefinition"/> so a typo cannot silently create a setting.
/// </summary>
public interface IClientSettings
{
    /// <summary>The stored value of <paramref name="key"/>, else its registered default (which may be null). Throws for an unregistered key.</summary>
    string? GetValue(string key);

    /// <summary>
    /// Stores <paramref name="value"/>; null clears the key back to its default. Returns false (and changes nothing) when the value
    /// is not one the definition allows. Throws for an unregistered key. Raises <see cref="Changed"/> when the effective value changes.
    /// </summary>
    bool TrySetValue(string key, string? value);

    /// <summary>Raised after the effective value of a key changes, from whichever thread called <see cref="TrySetValue"/>.</summary>
    event EventHandler<ClientSettingChangedEventArgs>? Changed;
}

/// <summary>A key whose effective value changed.</summary>
public sealed class ClientSettingChangedEventArgs(string key, string? value) : EventArgs
{
    public string Key { get; } = key;

    /// <summary>The new effective value (the default when the key was cleared).</summary>
    public string? Value { get; } = value;
}

/// <summary>A registered client setting.</summary>
/// <param name="Key">The key, for example <c>client.theme</c>.</param>
/// <param name="Default">The shipped default, or null when the platform decides (motion level).</param>
/// <param name="AllowedValues">The accepted values compared case-insensitively, or null to accept any non-empty text.</param>
public sealed record ClientSettingDefinition(string Key, string? Default, IReadOnlyList<string>? AllowedValues = null)
{
    /// <summary>True when <paramref name="value"/> is acceptable for this key.</summary>
    public bool Accepts(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && (AllowedValues is null || AllowedValues.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase));

    /// <summary>The stored form of an accepted value: the allowed value's own spelling, so "Graphite" and "graphite" are one value.</summary>
    public string Normalize(string value)
    {
        string trimmed = value.Trim();
        return AllowedValues?.FirstOrDefault(a => string.Equals(a, trimmed, StringComparison.OrdinalIgnoreCase)) ?? trimmed;
    }
}
