using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Client.Core.Mobile;

/// <summary>
/// The cached profile in <c>ui-profile.json</c> under a directory the head supplies (doc 27, cache file).
/// The file holds <c>{ "revision", "savedAt", "profile": { "paneKinds": [...] } }</c> and nothing else.
/// </summary>
/// <remarks>
/// <see cref="Save"/> writes <c>ui-profile.json.tmp</c>, flushes it to disk, and moves it over the main file, so a crash
/// never leaves a half-written profile. <see cref="Load"/> never throws for a missing, unreadable or corrupt file: it
/// returns null. A corrupt file is deleted, so the next save starts clean (doc 27: discarded silently).
/// Disk failures in <see cref="Save"/> are not caught; the caller decides whether a lost cache write matters.
/// </remarks>
public sealed class FileProfileCache : IProfileCache
{
    /// <summary>The cache file name inside the directory (doc 27).</summary>
    public const string FileName = "ui-profile.json";

    private readonly string _path;
    private readonly string _tempPath;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();

    /// <param name="directory">The directory that holds the cache file. It is created on the first save if missing.</param>
    /// <param name="clock">Source of the save time. Null uses the system clock.</param>
    public FileProfileCache(string directory, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _path = Path.Combine(directory, FileName);
        _tempPath = _path + ".tmp";
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public CachedProfile? Load()
    {
        lock (_gate)
        {
            // File.Exists is false for a directory, so a directory at the path counts as missing.
            if (!File.Exists(_path))
            {
                return null;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(_path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            var cached = Decode(bytes);
            if (cached is null)
            {
                TryDelete(_path);
            }

            return cached;
        }
    }

    /// <inheritdoc />
    public void Save(PhoneProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var file = new ProfileCacheFileDto(
            profile.Revision,
            _clock.GetUtcNow(),
            new ProfileBodyDto(profile.PaneKinds.Cast<string?>().ToList()));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file, ProfileCacheJson.Default.ProfileCacheFileDto);

        lock (_gate)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            try
            {
                using (var stream = new FileStream(_tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(_tempPath, _path, overwrite: true);
            }
            catch
            {
                TryDelete(_tempPath);
                throw;
            }
        }
    }

    private static CachedProfile? Decode(byte[] bytes)
    {
        ProfileCacheFileDto? file;
        try
        {
            file = JsonSerializer.Deserialize(bytes, ProfileCacheJson.Default.ProfileCacheFileDto);
        }
        catch (JsonException)
        {
            return null;
        }

        if (file is null || file.Revision is null || file.Revision < 0 || file.SavedAt is null)
        {
            return null;
        }

        var kinds = file.Profile?.PaneKinds;
        if (kinds is null || kinds.Any(kind => kind is null))
        {
            return null;
        }

        var profile = new PhoneProfile(file.Revision.Value, kinds.Select(kind => kind!).ToArray());
        return new CachedProfile(profile, file.SavedAt.Value);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort: a stale temp file is overwritten by the next save, and a corrupt file is re-read as no cache.
        }
    }
}

/// <summary>The cache file layout. Nullable so that a file missing a field is read as corrupt, not as zero.</summary>
internal sealed record ProfileCacheFileDto(long? Revision, DateTimeOffset? SavedAt, ProfileBodyDto? Profile);

/// <summary>The profile part of the cache file: pane kinds only.</summary>
internal sealed record ProfileBodyDto(List<string?>? PaneKinds);

/// <summary>
/// Source-generated JSON metadata for the profile cache. It is internal at namespace level, not nested in
/// <see cref="FileProfileCache"/>, because the source generator needs the containing type to be partial.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(ProfileCacheFileDto))]
internal sealed partial class ProfileCacheJson : JsonSerializerContext;
