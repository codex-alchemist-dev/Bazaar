using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Client.Preferences.Abstractions;

namespace Bazaar.Client.Preferences;

/// <summary>
/// Keeps the preferences in one JSON file, written to a temporary file and moved into place so a crash never leaves half a
/// file. A file that cannot be read is moved aside to <c>.bad</c> and the store starts from defaults.
/// </summary>
public sealed class FilePreferenceStorage : IPreferenceStorage
{
    private readonly string _path;
    private readonly Action<string>? _report;

    public FilePreferenceStorage(string path, Action<string>? report = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = path;
        _report = report;
    }

    public PreferenceSnapshot Load()
    {
        if (!File.Exists(_path))
        {
            return PreferenceSnapshot.Empty;
        }

        try
        {
            PreferenceFile? file = JsonSerializer.Deserialize(File.ReadAllText(_path), PreferenceJson.Default.PreferenceFile);
            return file is null ? PreferenceSnapshot.Empty : new PreferenceSnapshot(file.Values ?? [], file.Pending ?? []);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _report?.Invoke($"preferences: could not read '{_path}' ({ex.Message}); starting from defaults.");
            try
            {
                File.Move(_path, _path + ".bad", overwrite: true);
            }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
            {
                // Leave it; the next save replaces the file.
            }

            return PreferenceSnapshot.Empty;
        }
    }

    public void Save(PreferenceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temp = _path + ".tmp";
        var file = new PreferenceFile(1, new Dictionary<string, string>(snapshot.Values), [.. snapshot.Pending]);
        File.WriteAllText(temp, JsonSerializer.Serialize(file, PreferenceJson.Default.PreferenceFile));
        File.Move(temp, _path, overwrite: true);
    }
}

internal sealed record PreferenceFile(int Version, Dictionary<string, string>? Values, List<string>? Pending);

[JsonSerializable(typeof(PreferenceFile))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class PreferenceJson : JsonSerializerContext;
