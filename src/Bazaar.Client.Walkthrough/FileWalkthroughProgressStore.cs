using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Client.Walkthrough;

/// <summary>
/// A local cache of the walkthrough's progress in one JSON file (doc 45 section 2.3: a local cache is a convenience, the app works
/// with it empty). A missing, unreadable or corrupt file reads as "no progress". The account-wide copy is <c>prefs.set</c>'s.
/// </summary>
public sealed class FileWalkthroughProgressStore(string path) : IWalkthroughProgressStore
{
    private readonly string _path = string.IsNullOrWhiteSpace(path) ? throw new ArgumentException("A file path is required.", nameof(path)) : path;

    public async Task<WalkthroughProgress?> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = File.OpenRead(_path);
            var file = await JsonSerializer.DeserializeAsync(stream, WalkthroughFileJson.Default.ProgressFile, cancellationToken).ConfigureAwait(false);
            if (file is null) return null;
            var current = Enum.IsDefined(file.Current) ? file.Current : WalkthroughStep.Welcome;
            return new WalkthroughProgress(file.Dismissed, file.Finished, current, [.. file.Completed.Where(Enum.IsDefined)]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public async Task SaveAsync(WalkthroughProgress progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (Path.GetDirectoryName(Path.GetFullPath(_path)) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
        var temp = _path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, new ProgressFile(progress.Dismissed, progress.Finished, progress.Current, [.. progress.Completed]), WalkthroughFileJson.Default.ProgressFile, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temp, _path, overwrite: true);
    }

    internal sealed record ProgressFile(bool Dismissed, bool Finished, WalkthroughStep Current, WalkthroughStep[] Completed);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(FileWalkthroughProgressStore.ProgressFile))]
internal sealed partial class WalkthroughFileJson : JsonSerializerContext;
