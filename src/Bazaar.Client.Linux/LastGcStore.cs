using System.Text.Json;

namespace Bazaar.Client.Linux;

/// <summary>
/// The group chat the head opened last and the server it was on (doc 30). It sits beside <c>session.bin</c> in the state directory
/// and is written with mode 0600. It holds no secret. A missing, unreadable or corrupt file reads as "nothing remembered".
/// </summary>
internal static class LastGcStore
{
    /// <summary>What was remembered.</summary>
    internal sealed record Entry(Guid GcId, string Server);

    private sealed record Stored(string? Gc, string? Server);

    public static Entry? Load(string path)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path));
            return stored is { Gc: { } gc, Server: { Length: > 0 } server } && Guid.TryParse(gc, out var id) && id != Guid.Empty ? new Entry(id, server) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes the entry. A failure is returned as false: remembering is a convenience and never blocks a start.</summary>
    public static bool Save(string path, Entry entry)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(new Stored(entry.GcId.ToString("D"), entry.Server));
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(path, json);
                return true;
            }

            // Create at 0600 so the file is never readable by others, even briefly.
            using (var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            }))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
            }

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
