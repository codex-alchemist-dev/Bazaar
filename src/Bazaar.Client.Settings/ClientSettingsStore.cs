using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Client.Settings;

[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class ClientSettingsJsonContext : JsonSerializerContext;

/// <summary>
/// The client settings store: registered definitions with defaults, change notification, and optionally a JSON file that keeps the
/// person's values between runs. A missing, unreadable or malformed file gives the defaults (the file is never the reason the client
/// fails to start); values the definitions no longer accept are dropped on load. Writes go to a temp file and replace the original.
/// </summary>
public sealed class ClientSettingsStore : IClientSettings
{
    private readonly Dictionary<string, ClientSettingDefinition> _definitions;
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly string? _path;
    private readonly Action<string>? _log;
    private readonly Lock _gate = new();

    /// <summary>Creates a store. With a <paramref name="path"/> it loads that file now and saves to it on every change.</summary>
    public ClientSettingsStore(IEnumerable<ClientSettingDefinition> definitions, string? path = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        _definitions = definitions.ToDictionary(d => d.Key, StringComparer.Ordinal);
        _path = path;
        _log = log;
        Load();
    }

    /// <inheritdoc />
    public event EventHandler<ClientSettingChangedEventArgs>? Changed;

    /// <inheritdoc />
    public string? GetValue(string key)
    {
        ClientSettingDefinition definition = Definition(key);
        lock (_gate)
        {
            return _values.TryGetValue(key, out string? value) ? value : definition.Default;
        }
    }

    /// <inheritdoc />
    public bool TrySetValue(string key, string? value)
    {
        ClientSettingDefinition definition = Definition(key);
        if (value is not null && !definition.Accepts(value))
        {
            return false;
        }

        string? after;
        lock (_gate)
        {
            string? before = _values.TryGetValue(key, out string? old) ? old : definition.Default;
            if (value is null)
            {
                _values.Remove(key);
            }
            else
            {
                _values[key] = definition.Normalize(value);
            }

            after = _values.TryGetValue(key, out string? now) ? now : definition.Default;
            if (string.Equals(before, after, StringComparison.Ordinal))
            {
                return true;
            }

            Save();
        }

        Changed?.Invoke(this, new ClientSettingChangedEventArgs(key, after));
        return true;
    }

    private ClientSettingDefinition Definition(string key) =>
        _definitions.TryGetValue(key, out ClientSettingDefinition? definition)
            ? definition
            : throw new ArgumentException($"'{key}' is not a registered client setting.", nameof(key));

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            Dictionary<string, string>? stored = JsonSerializer.Deserialize(File.ReadAllText(_path), ClientSettingsJsonContext.Default.DictionaryStringString);
            foreach ((string key, string value) in stored ?? [])
            {
                if (_definitions.TryGetValue(key, out ClientSettingDefinition? definition) && definition.Accepts(value))
                {
                    _values[key] = definition.Normalize(value);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log?.Invoke($"Client settings file '{_path}' was not read, using defaults: {ex.Message}");
        }
    }

    // Called with the gate held. A failed save keeps the value for this run and says so.
    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_values, ClientSettingsJsonContext.Default.DictionaryStringString));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Client settings file '{_path}' was not saved: {ex.Message}");
        }
    }
}
