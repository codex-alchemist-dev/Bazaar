namespace Bazaar.Server.GraphRetrieval;

internal static class ParallelChunks
{
    public static int ChunkCount(int total, int chunkSize) => (total + chunkSize - 1) / chunkSize;

    /// <summary>
    /// Runs <paramref name="body"/>(chunkIndex, start, end) over fixed-size chunks. Chunk boundaries never depend on the
    /// thread count, so callers that merge chunk results in chunk order are deterministic.
    /// </summary>
    public static void Run(int total, int chunkSize, int threads, Action<int, int, int> body, CancellationToken ct)
    {
        int chunks = ChunkCount(total, chunkSize);
        if (chunks == 0)
        {
            return;
        }

        if (threads <= 1 || chunks == 1)
        {
            for (int c = 0; c < chunks; c++)
            {
                ct.ThrowIfCancellationRequested();
                body(c, c * chunkSize, Math.Min(total, (c + 1) * chunkSize));
            }

            return;
        }

        Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct }, c =>
            body(c, c * chunkSize, Math.Min(total, (c + 1) * chunkSize)));
    }
}
