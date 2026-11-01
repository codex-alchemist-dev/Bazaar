namespace Bazaar.Client.LocalInference.Benchmark;

/// <summary>
/// Runs every corpus item on every backend as a <see cref="LocalTaskKind.VoiceCleanup"/> request (doc 31, Testing: hardware lane).
/// Recorded, not gated: a failing or unfaithful run is recorded, never thrown. Only cancellation propagates.
/// </summary>
public sealed class BenchmarkRunner
{
    private const int _maxOutputTokens = 256;

    private readonly TimeProvider _time;

    public BenchmarkRunner(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public async Task<BenchmarkReport> RunAsync(IReadOnlyList<ILocalModelBackend> backends, IReadOnlyList<CorpusItem> corpus, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(backends);
        ArgumentNullException.ThrowIfNull(corpus);

        var records = new List<BenchmarkRecord>();
        foreach (var backend in backends)
        {
            foreach (var item in corpus)
            {
                records.Add(await RunItemAsync(backend, item, ct));
            }
        }

        return BenchmarkReport.From(records, _time.GetUtcNow());
    }

    private async Task<BenchmarkRecord> RunItemAsync(ILocalModelBackend backend, CorpusItem item, CancellationToken ct)
    {
        var request = new LocalTaskRequest(LocalTaskKind.VoiceCleanup, item.Input, _maxOutputTokens, $"bench-{item.Id}");
        var start = _time.GetTimestamp();
        try
        {
            var result = await backend.RunAsync(request, ct);
            return new BenchmarkRecord(
                item.Id,
                result.Tier,
                ElapsedMs(start),
                result.Degraded,
                null,
                KeepsAllTokens(item, result.Text));
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            return new BenchmarkRecord(item.Id, backend.Tier, ElapsedMs(start), false, $"{error.GetType().Name}: {error.Message}", false);
        }
    }

    private double ElapsedMs(long start) => _time.GetElapsedTime(start).TotalMilliseconds;

    private static bool KeepsAllTokens(CorpusItem item, string text) =>
        item.MustKeep.All(token => text.Contains(token, StringComparison.Ordinal));
}
