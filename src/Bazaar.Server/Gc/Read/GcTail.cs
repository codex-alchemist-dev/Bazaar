using System.Runtime.CompilerServices;
using Bazaar.Contracts.Gc;

namespace Bazaar.Server.Gc.Read;

/// <summary>How a tail pages and sweeps.</summary>
/// <param name="PageSize">Events read per query.</param>
/// <param name="SweepInterval">Longest wait without a hint before the tail reads anyway, so a lost hint costs at most this much latency.</param>
public sealed record GcTailOptions(int PageSize, TimeSpan SweepInterval)
{
    public static GcTailOptions Default { get; } = new(200, TimeSpan.FromSeconds(5));
}

/// <summary>
/// Follows one GC live: reads the log from a cursor, then waits for a hint and reads again. The hint is only a wake-up, so the
/// stream equals the log whatever the listener missed: a reconnect resync, the periodic sweep and the first read all go to
/// the table. The cursor advances over the seqs actually read, which are contiguous (see <see cref="IGcLogReader.ReadAsync"/>).
/// </summary>
public sealed class GcTail(IGcLogReader reader, GcNotifyListener listener, GcTailOptions? options = null)
{
    private readonly GcTailOptions _options = options ?? GcTailOptions.Default;

    /// <summary>Yields every event after <paramref name="afterSeq"/>, then new ones as they commit, until cancelled.</summary>
    public async IAsyncEnumerable<GcEvent> FollowAsync(
        Guid gcId, long afterSeq, GcPrincipal viewer, [EnumeratorCancellation] CancellationToken ct)
    {
        // Subscribe before the first read so a commit between the read and the wait leaves a wake behind.
        using var wakes = listener.Subscribe(gcId);
        var cursor = afterSeq;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = 0;
            await foreach (var e in reader.ReadAsync(gcId, cursor, _options.PageSize, viewer, ct).ConfigureAwait(false))
            {
                cursor = e.Seq;
                read++;
                yield return e;
            }

            if (read == _options.PageSize)
                continue;
            await wakes.WaitAsync(_options.SweepInterval, listener.Time, ct).ConfigureAwait(false);
        }
    }
}
