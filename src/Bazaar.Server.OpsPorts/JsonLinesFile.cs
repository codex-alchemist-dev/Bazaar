using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Bazaar.Client.Tui.Dashboard;

namespace Bazaar.Server.OpsPorts;

/// <summary>One log line as stored: a JSON object per line (<c>at</c>, <c>level</c>, <c>module</c>, <c>message</c>).</summary>
internal sealed record LogLineRecord(DateTimeOffset At, string Level, string Module, string Message);

/// <summary>One health event as stored.</summary>
internal sealed record EventRecord(string Id, DateTimeOffset At, string Kind, string Summary, bool Major);

/// <summary>One TUI crash as stored.</summary>
internal sealed record CrashRecord(DateTimeOffset At, string Reason);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(LogLineRecord))]
[JsonSerializable(typeof(EventRecord))]
[JsonSerializable(typeof(CrashRecord))]
internal sealed partial class OpsJsonContext : JsonSerializerContext;

/// <summary>
/// Append-only JSON-lines files, one object per line, shared by a writer and any number of readers. A reader takes only the tail of
/// the file, so a large file never costs more than the configured byte cap, and a line it cannot parse (a torn write, or a foreign
/// line) is skipped, never thrown.
/// </summary>
internal static class JsonLinesFile
{
    private static readonly Lock _writeLock = new();

    public static void Append(string path, string json)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        var options = new FileStreamOptions
        {
            Mode = FileMode.Append,
            Access = FileAccess.Write,
            Share = FileShare.ReadWrite | FileShare.Delete,
        };
        if (!OperatingSystem.IsWindows())
        {
            // A new file is readable by its owner and group only; on Windows the folder's ACL decides.
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        }

        lock (_writeLock)
        {
            using var stream = new FileStream(path, options);
            stream.Write(bytes);
        }
    }

    /// <summary>The complete lines in the last <paramref name="maxBytes"/> of the file, oldest first. Empty when the file does not exist.</summary>
    public static IReadOnlyList<string> ReadTail(string path, int maxBytes)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var start = Math.Max(0, length - maxBytes);
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[length - start];
            stream.ReadExactly(buffer);
            var lines = Encoding.UTF8.GetString(buffer).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // A cut in the middle of a line leaves a partial first line: drop it.
            return start > 0 && lines.Length > 0 ? lines[1..] : lines;
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static T? TryParse<T>(string line, JsonTypeInfo<T> info)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(line, info);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static OpsLogLevel ParseLevel(string? level) =>
        Enum.TryParse<OpsLogLevel>(level, ignoreCase: true, out var parsed) ? parsed : OpsLogLevel.Information;

    public static OpsEventKind ParseKind(string? kind) =>
        Enum.TryParse<OpsEventKind>(kind, ignoreCase: true, out var parsed) ? parsed : OpsEventKind.Info;
}
