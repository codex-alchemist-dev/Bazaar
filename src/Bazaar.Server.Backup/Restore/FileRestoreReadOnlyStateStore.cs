using System.Text.Json;
using Bazaar.Server.Backup.Archiving;

namespace Bazaar.Server.Backup.Restore;

/// <summary>
/// Keeps the "this install is read-only because of restore X" record in one small file (doc 48 section 6). A write goes
/// to a temporary file in the same directory, is flushed to disk, is renamed over the record, and on POSIX the directory
/// is flushed too, so a crash leaves either the old record or the new one and never a partial one.
/// </summary>
/// <remarks>
/// Fail closed: the host must treat a present record, even an unreadable one, as read-only. <see cref="IsReadOnly"/> is
/// true whenever the file exists. The file holds the restore id only, never secret material.
/// </remarks>
public sealed class FileRestoreReadOnlyStateStore : IRestoreReadOnlyStateStore
{
    private readonly string _path;
    private readonly Action<string>? _afterTempWritten;
    private readonly Lock _lock = new();

    /// <param name="path">The state file. Its directory is created when missing.</param>
    public FileRestoreReadOnlyStateStore(string path)
        : this(path, null)
    {
    }

    /// <summary>Test seam: <paramref name="afterTempWritten"/> runs after the temporary file is durable and before the rename.</summary>
    public FileRestoreReadOnlyStateStore(string path, Action<string>? afterTempWritten)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = Path.GetFullPath(path);
        _afterTempWritten = afterTempWritten;
    }

    /// <summary>True when a read-only record exists, readable or not.</summary>
    public bool IsReadOnly() => File.Exists(_path);

    /// <summary>The restore id on record, or null when there is no record.</summary>
    /// <exception cref="InvalidDataException">The record exists but cannot be read. Treat the install as read-only.</exception>
    public Task<string?> ReadRestoreIdAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            return Task.FromResult(ReadId());
        }
    }

    /// <inheritdoc />
    public Task PersistAsync(string restoreId, CancellationToken ct)
    {
        ValidateId(restoreId);
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new StateRecord(1, restoreId), StateJson.Default.StateRecord);
            DurableFile.WriteAtomically(_path, bytes, _afterTempWritten);
        }

        return Task.CompletedTask;
    }

    /// <summary>Removes the record. A record that names another restore is left in place and an exception is thrown.</summary>
    /// <exception cref="InvalidOperationException">The record belongs to a different restore.</exception>
    public Task ClearAsync(string restoreId, CancellationToken ct)
    {
        ValidateId(restoreId);
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (!File.Exists(_path))
            {
                return Task.CompletedTask;
            }

            var current = ReadId();
            if (!string.Equals(current, restoreId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The read-only record belongs to a different restore and was not cleared.");
            }

            File.Delete(_path);
            DurableFile.FsyncDirectory(Path.GetDirectoryName(_path)!);
        }

        return Task.CompletedTask;
    }

    private string? ReadId()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            var record = JsonSerializer.Deserialize(File.ReadAllBytes(_path), StateJson.Default.StateRecord);
            if (record is null || record.Version != 1 || string.IsNullOrWhiteSpace(record.RestoreId))
            {
                throw new InvalidDataException("The restore read-only record is not valid.");
            }

            return record.RestoreId;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The restore read-only record is not valid.", ex);
        }
    }

    private static void ValidateId(string restoreId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(restoreId);
    }

    internal sealed record StateRecord(int Version, string RestoreId);
}

[System.Text.Json.Serialization.JsonSerializable(typeof(FileRestoreReadOnlyStateStore.StateRecord))]
internal sealed partial class StateJson : System.Text.Json.Serialization.JsonSerializerContext
{
}
