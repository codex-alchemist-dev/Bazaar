using System.Text.Json;

namespace Bazaar.Client.Updater.Linux;

/// <summary>The persisted record is unreadable. The updater fails closed rather than guess.</summary>
internal sealed class RollbackRecordException : Exception
{
    public RollbackRecordException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// The highest version the install was rolled back from, kept in <c>rolled-back.json</c> under the install root as
/// <c>{"version":"x.y.z"}</c>. Versions up to and including it are not offered again; a newer one is. Written to a
/// temporary owner-only file and renamed over the old one, so a reader sees the old or the new record, never a torn one.
/// </summary>
internal sealed class RollbackRecord
{
    private readonly IUpdateFileSystem _fs;
    private readonly string _path;

    public RollbackRecord(IUpdateFileSystem fs, string installRoot)
    {
        _fs = fs;
        _path = Path.Combine(installRoot, "rolled-back.json");
    }

    /// <summary>The recorded version, or null when none was written.</summary>
    /// <exception cref="RollbackRecordException">The file exists but is not a valid record.</exception>
    public string? Read()
    {
        if (!_fs.FileExists(_path))
        {
            return null;
        }

        try
        {
            using var stream = _fs.OpenRead(_path);
            using var json = JsonDocument.Parse(stream);
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("version", out var element)
                && element.ValueKind == JsonValueKind.String
                && element.GetString() is { } version
                && SemVerPrecedence.TryParse(version, out _, out _))
            {
                return version;
            }
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            throw new RollbackRecordException($"The rollback record '{_path}' cannot be read: {e.Message}", e);
        }

        throw new RollbackRecordException($"The rollback record '{_path}' does not hold a valid version. Remove it only after checking which release was rolled back.");
    }

    /// <summary>Records <paramref name="version"/>, keeping the higher one when a valid record exists. An unreadable old record is replaced.</summary>
    public void Write(string version)
    {
        try
        {
            string? existing = Read();
            if (existing is not null && SemVerPrecedence.Compare(existing, version) >= 0)
            {
                return;
            }
        }
        catch (RollbackRecordException)
        {
            // Rolling back is the recovery action; the new record replaces the damaged one.
        }

        string temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        using (var stream = _fs.CreateFile(temp))
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("version", version);
            writer.WriteEndObject();
        }

        try
        {
            _fs.RenameOver(temp, _path);
        }
        catch
        {
            _fs.DeleteFile(temp);
            throw;
        }
    }
}
