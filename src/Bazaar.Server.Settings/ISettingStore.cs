using System.Text.Json;

namespace Bazaar.Server.Settings;

/// <summary>
/// Reads and writes setting values (doc 03). Reads return the key's default when unset. Writes are validated against the
/// key's type, validator, floors and approval rule; a write that is not accepted changes nothing and says why in the result.
/// </summary>
public interface ISettingStore
{
    /// <summary>The effective value: the stored one, or the key's default. Throws if a stored value is invalid.</summary>
    Task<T> GetAsync<T>(SettingKey<T> key, string scopeId = "", CancellationToken cancellationToken = default);

    /// <summary>Writes a typed value. Serialises it with the key's wire form and runs the same checks as <see cref="SetJsonAsync"/>.</summary>
    Task<SettingWriteResult> SetAsync<T>(SettingKey<T> key, T value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default);

    /// <summary>Writes an untyped value by key name (setup wizard, admin CLI). Unknown names and wrong types are rejected.</summary>
    Task<SettingWriteResult> SetJsonAsync(string key, JsonElement value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default);
}
