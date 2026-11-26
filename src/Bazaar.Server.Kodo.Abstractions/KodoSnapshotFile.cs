using Bazaar.Contracts.Kodo;

namespace Bazaar.Server.Kodo;

/// <summary>
/// The latest Kodo snapshot as a file, so a process that does not run the monitor (the resident TUI, doc 58) can read it. The Kodo
/// server module writes it after every evaluation; the file holds exactly the wire snapshot of <c>/api/kodo/v1/status</c> (probe ids,
/// wire statuses, cause codes and hashes, doc 46 section 15), so it carries nothing the API would not. It is replaced atomically.
/// </summary>
public static class KodoSnapshotFile
{
    public const string FileName = "kodo-status.json";

    /// <summary>The path when the operator names none: <c>Bazaar/kodo</c> under the machine-wide application data folder.</summary>
    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Bazaar", "kodo", FileName);

    /// <summary>The operator's explicit path, else a file in the Kodo data directory, else <see cref="DefaultPath"/>.</summary>
    public static string ResolvePath(string? explicitPath, string? dataDirectory) =>
        !string.IsNullOrWhiteSpace(explicitPath) ? explicitPath.Trim()
        : !string.IsNullOrWhiteSpace(dataDirectory) ? Path.Combine(dataDirectory.Trim(), FileName)
        : DefaultPath();

    /// <summary>Writes the snapshot through a temporary file and a rename, so a reader never sees half a file.</summary>
    public static void Write(string path, KodoStatusSnapshotDto snapshot)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(snapshot);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temp = path + ".tmp";
        File.WriteAllBytes(temp, KodoWireCodec.EncodeSnapshot(snapshot));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Reads the snapshot. Null when the file is missing or does not hold a snapshot.</summary>
    public static KodoStatusSnapshotDto? TryRead(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            return File.Exists(path) ? KodoWireCodec.DecodeSnapshot(File.ReadAllBytes(path)) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
