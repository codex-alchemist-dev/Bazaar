using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Core.Wire;

/// <summary>
/// Cursors persisted to a JSON file, so replay after a restart resumes from the client's own cursor (doc 02).
/// The file holds cursors only, never GC content.
/// </summary>
/// <remarks>
/// The file is loaded once at construction. <see cref="Save"/> updates memory and then rewrites the whole file:
/// the JSON goes to <c>path + ".tmp"</c>, is flushed to disk, and is moved over <c>path</c>, so a crash never
/// leaves a half-written main file. Writes are serialised by a lock; the last write wins.
/// <para>
/// A missing file starts empty. A corrupt, empty or unknown-version file also starts empty, and it is renamed
/// to <c>path + ".corrupt"</c> so it is not silently overwritten by the next save. A later rename replaces an
/// earlier <c>.corrupt</c> copy. Unknown JSON fields are ignored.
/// </para>
/// <para>
/// Disk failures in <see cref="Save"/> never throw. Memory is still updated, the failure is kept in
/// <see cref="LastWriteError"/>, and the next save retries the write.
/// </para>
/// </remarks>
public sealed class FileCursorStore : ICursorStore
{
    private const int FormatVersion = 1;

    private readonly string _path;
    private readonly string _tempPath;
    private readonly Dictionary<string, StreamCursor> _cursors = [];
    private readonly Lock _gate = new();
    private Exception? _lastWriteError;

    /// <summary>Opens the cursor file at <paramref name="path"/>, loading any cursors already stored there.</summary>
    /// <param name="path">The cursor file. Its parent directory is created on the first save if missing.</param>
    public FileCursorStore(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        _path = path;
        _tempPath = path + ".tmp";
        LoadFromDisk();
    }

    /// <summary>The last disk error from a save (or from quarantining a bad file), or null after a successful save.</summary>
    public Exception? LastWriteError
    {
        get
        {
            lock (_gate)
            {
                return _lastWriteError;
            }
        }
    }

    /// <inheritdoc />
    public StreamCursor Load(string stream)
    {
        lock (_gate)
        {
            return _cursors.TryGetValue(stream, out var c) ? c : new StreamCursor(stream, 0, 0);
        }
    }

    /// <inheritdoc />
    public void Save(StreamCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        lock (_gate)
        {
            _cursors[cursor.S] = cursor;
            try
            {
                WriteAll();
                _lastWriteError = null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _lastWriteError = e;
            }
        }
    }

    private void LoadFromDisk()
    {
        // File.Exists is false for a directory, so a directory at the path is treated as missing.
        if (!File.Exists(_path))
        {
            return;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(_path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _lastWriteError = e;
            return;
        }

        CursorFileDto? file;
        try
        {
            file = JsonSerializer.Deserialize(bytes, CursorJson.Default.CursorFileDto);
        }
        catch (JsonException)
        {
            file = null;
        }

        if (file is null || file.Version != FormatVersion)
        {
            Quarantine();
            return;
        }

        foreach (var entry in file.Cursors ?? [])
        {
            if (!string.IsNullOrEmpty(entry.S))
            {
                _cursors[entry.S] = new StreamCursor(entry.S, entry.After, entry.EditAfter);
            }
        }
    }

    private void Quarantine()
    {
        try
        {
            File.Move(_path, _path + ".corrupt", overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _lastWriteError = e;
        }
    }

    private void WriteAll()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var file = new CursorFileDto(
            FormatVersion,
            _cursors.Values
                .OrderBy(c => c.S, StringComparer.Ordinal)
                .Select(c => new CursorEntryDto(c.S, c.After, c.EditAfter))
                .ToList());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file, CursorJson.Default.CursorFileDto);

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
            TryDeleteTemp();
            throw;
        }
    }

    private void TryDeleteTemp()
    {
        try
        {
            File.Delete(_tempPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort: a stale temp file is ignored on load and overwritten by the next save.
        }
    }
}

/// <summary>The file layout: <c>{"version":1,"cursors":[{"s":"gc:...","after":11,"editAfter":39}]}</c>.</summary>
internal sealed record CursorFileDto(int Version, List<CursorEntryDto>? Cursors);

/// <summary>One stored cursor.</summary>
internal sealed record CursorEntryDto(string? S, long After, long EditAfter);

/// <summary>
/// Source-generated JSON metadata for the cursor file. It is internal at namespace level, not nested in
/// <see cref="FileCursorStore"/>, because the source generator needs the containing type to be partial.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(CursorFileDto))]
internal sealed partial class CursorJson : JsonSerializerContext;
