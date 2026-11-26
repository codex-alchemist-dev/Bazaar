namespace Bazaar.Client.OfflineSync;

/// <summary>
/// An exclusive hold on a queue directory (doc 56 open item 9). The operating system owns the lock: it is a file opened with no
/// sharing, so a crashed process releases it and there is never a stale lock to clean up.
/// </summary>
internal sealed class OfflineQueueDirectoryLock : IDisposable
{
    /// <summary>The lock file name inside the queue directory.</summary>
    internal const string FileName = "offline-queue.lock";

    private readonly FileStream _handle;

    private OfflineQueueDirectoryLock(FileStream handle) => _handle = handle;

    /// <summary>Takes the lock on <paramref name="directory"/>.</summary>
    /// <param name="directory">An existing queue directory.</param>
    /// <returns>The held lock; dispose it to release.</returns>
    /// <exception cref="OfflineQueueLockedException">The directory is already locked.</exception>
    internal static OfflineQueueDirectoryLock Acquire(string directory)
    {
        var path = Path.Combine(directory, FileName);
        try
        {
            return new OfflineQueueDirectoryLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None));
        }
        catch (IOException ex)
        {
            throw new OfflineQueueLockedException(directory, ex);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _handle.Dispose();
}
