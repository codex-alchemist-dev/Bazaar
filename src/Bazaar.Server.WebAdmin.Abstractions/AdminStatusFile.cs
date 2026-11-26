using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.WebAdmin;

/// <summary>What the server host writes for <see cref="AdminStatusFile"/>: the status view and when it was written.</summary>
public sealed record AdminStatusFileContent(AdminStatusView Status, DateTimeOffset WrittenAt);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(AdminStatusFileContent))]
internal sealed partial class AdminStatusFileJson : JsonSerializerContext;

/// <summary>
/// The server's status view as a file, so a process that is not the server (the resident TUI, doc 58) can read it. The server host
/// writes it on a timer; it holds exactly what <c>GET /admin/api/status</c> returns (version, start time, provider lines; no secret) and
/// is replaced atomically. <see cref="Read"/> turns it back into an <see cref="AdminStatusView"/>, and says plainly when the server has
/// stopped reporting.
/// </summary>
public static class AdminStatusFile
{
    public const string FileName = "server-status.json";

    /// <summary>How long without a write before the reader says the server is not reporting. The host writes every 15 seconds.</summary>
    public static TimeSpan DefaultStaleAfter { get; } = TimeSpan.FromSeconds(60);

    /// <summary>The path when the operator names none: <c>Bazaar/ops</c> under the machine-wide application data folder, the TUI's own data directory.</summary>
    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Bazaar", "ops", FileName);

    /// <summary>The operator's explicit path, else a file in the ops data directory, else <see cref="DefaultPath"/>.</summary>
    public static string ResolvePath(string? explicitPath, string? opsDataDirectory) =>
        !string.IsNullOrWhiteSpace(explicitPath) ? explicitPath.Trim()
        : !string.IsNullOrWhiteSpace(opsDataDirectory) ? Path.Combine(opsDataDirectory.Trim(), FileName)
        : DefaultPath();

    /// <summary>Writes the view through a temporary file and a rename, so a reader never sees half a file.</summary>
    public static void Write(string path, AdminStatusView status, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(status);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new AdminStatusFileContent(status, now), AdminStatusFileJson.Default.AdminStatusFileContent));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Reads the file at <paramref name="now"/>. A fresh file gives the view with uptime counted to now. A stale file gives the last
    /// known view with zero uptime and a first provider line saying the server is not reporting. A missing or unreadable file throws
    /// <see cref="InvalidOperationException"/>, so the section reads "unavailable".
    /// </summary>
    public static AdminStatusView Read(string path, DateTimeOffset now, TimeSpan staleAfter)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        AdminStatusFileContent? content;
        try
        {
            content = JsonSerializer.Deserialize(File.ReadAllText(path), AdminStatusFileJson.Default.AdminStatusFileContent);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidOperationException("The server status file cannot be read.", error);
        }

        if (content?.Status is not { } status)
        {
            throw new InvalidOperationException("The server status file is empty.");
        }

        TimeSpan age = now - content.WrittenAt;
        if (age > staleAfter)
        {
            return status with
            {
                UptimeSeconds = 0,
                ProviderLines = [$"Server is not reporting (last report {(long)age.TotalSeconds} seconds ago)", .. status.ProviderLines],
            };
        }

        return status with { UptimeSeconds = Math.Max(0, (long)(now - status.StartedAt).TotalSeconds) };
    }
}
