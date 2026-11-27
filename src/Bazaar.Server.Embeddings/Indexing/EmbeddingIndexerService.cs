using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Embeddings.Indexing;

/// <summary>Runs <see cref="EmbeddingIndexer"/> on an interval. With <c>memory.embedding_profile</c> at <c>none</c> every sweep is a no-op.</summary>
public sealed partial class EmbeddingIndexerService(
    EmbeddingIndexer indexer,
    IOptions<EmbeddingOptions> options,
    ILogger<EmbeddingIndexerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.IndexerIntervalSeconds));
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                var result = await indexer.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                if (result.Error is not null)
                {
                    LogDeferred(result.Error);
                }

                if (result.Embedded > 0 || result.TooLong > 0)
                {
                    LogEmbedded(result.Embedded, result.SkippedPersonal, result.TooLong, result.RetryLater);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A database or other fault must not stop the worker; the next sweep retries.
                LogFailed(ex);
            }
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            return await timer.WaitForNextTickAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Embedding sweep deferred: {Reason}")]
    private partial void LogDeferred(string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Embedded {Count} Vault blocks ({Skipped} personal-data blocks skipped, {TooLong} too long, {RetryLater} deferred)")]
    private partial void LogEmbedded(int count, int skipped, int tooLong, int retryLater);

    [LoggerMessage(Level = LogLevel.Error, Message = "Embedding sweep failed")]
    private partial void LogFailed(Exception exception);
}
