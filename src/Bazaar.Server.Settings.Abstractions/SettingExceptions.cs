namespace Bazaar.Server.Settings;

/// <summary>A value in storage is not valid for its key (wrong JSON type, or it breaks a floor). Reads fail closed.</summary>
public sealed class InvalidSettingValueException(string key, string reason)
    : Exception($"Stored value of setting '{key}' is invalid: {reason}")
{
    public string Key { get; } = key;
}
