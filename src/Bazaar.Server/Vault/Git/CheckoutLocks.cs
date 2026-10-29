using System.Collections.Concurrent;

namespace Bazaar.Server.Vault.Git;

/// <summary>
/// In-process writer locks, one per checkout path (the single-writer rule on <see cref="IGitProvider"/>). Paths are
/// normalised before use, so the same checkout reached through two spellings shares one lock.
/// </summary>
internal static class CheckoutLocks
{
    private static readonly StringComparer _pathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(_pathComparer);

    /// <summary>Waits for the checkout's lock. Dispose the returned value to release it.</summary>
    public static async Task<IDisposable> AcquireAsync(string checkoutPath, CancellationToken ct)
    {
        var key = Normalize(checkoutPath);
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new Lease(gate);
    }

    internal static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

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
