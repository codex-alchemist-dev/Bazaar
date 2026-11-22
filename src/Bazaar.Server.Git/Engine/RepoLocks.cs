using System.Collections.Concurrent;

namespace Bazaar.Server.Git;

/// <summary>
/// Per-repo async locks (spike S23: one writer per checkout) plus one merge lane per repo. Waiting never blocks a thread,
/// honours the cancellation token, and fails with <see cref="GitLockTimeoutException"/> after the configured timeout.
/// Dispose the returned lease to release; it releases once even if disposed twice.
/// </summary>
public sealed class RepoLocks(TimeSpan timeout)
{
    private static readonly StringComparer _pathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(_pathComparer);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _lanes = new(_pathComparer);

    /// <summary>The lock key of a workspace or repo path, normalised so two spellings share one lock.</summary>
    public static string KeyForPath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>Waits for the repo (or workspace) lock under <paramref name="key"/>.</summary>
    public Task<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken) =>
        AcquireAsync(_locks, key, cancellationToken);

    /// <summary>Waits for the repo's single merge lane. It is independent of <see cref="AcquireAsync(string, CancellationToken)"/>; a merge takes both.</summary>
    public Task<IDisposable> AcquireMergeLaneAsync(string key, CancellationToken cancellationToken) =>
        AcquireAsync(_lanes, key, cancellationToken);

    private async Task<IDisposable> AcquireAsync(ConcurrentDictionary<string, SemaphoreSlim> map, string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var gate = map.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        // WaitAsync throws OperationCanceledException on cancel without taking the lock, so nothing leaks on that path.
        if (!await gate.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
        {
            throw new GitLockTimeoutException(key, timeout);
        }

        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
