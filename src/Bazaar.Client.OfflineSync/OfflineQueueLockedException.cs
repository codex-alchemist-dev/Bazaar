namespace Bazaar.Client.OfflineSync;

/// <summary>
/// Another process (or another queue instance in this process) already holds the queue directory (doc 56 open item 9).
/// Two writers on one queue file would interleave appends and break the sequence, so the second open is refused.
/// </summary>
public sealed class OfflineQueueLockedException : InvalidOperationException
{
    /// <summary>Creates the exception for the locked directory.</summary>
    /// <param name="directory">The queue directory that is already open elsewhere.</param>
    /// <param name="inner">The file-sharing failure, when there is one.</param>
    public OfflineQueueLockedException(string directory, Exception? inner = null)
        : base($"The offline queue directory '{directory}' is already open by another queue.", inner)
    {
    }
}
