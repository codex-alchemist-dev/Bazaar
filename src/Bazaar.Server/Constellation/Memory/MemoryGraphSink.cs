using Bazaar.Server.Memory.Notes;
using Bazaar.Server.Memory.Write;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Constellation.Memory;

/// <summary>
/// Decorator over the Memory module's batch sink (docs/design/constellation-memory-graph.md, "Sink"). After the inner
/// commit succeeds, every note in the batch is parsed, projected and stored as a graph revision.
/// </summary>
/// <remarks>
/// The Vault is the source of truth. Nothing on the projection path throws to the caller: a parse failure or a store
/// failure for one item is logged and the other items still store. The inner result is returned unchanged.
/// Known limit: wiki-links resolve only to note ids today. Title resolution needs the Vault index, so
/// <c>noteIdsByTitle</c> is empty and a <c>[[Title]]</c> link produces no edge until that index is wired.
/// </remarks>
public sealed partial class MemoryGraphSink(IMemoryBatchSink inner, IMemoryGraphStore store, ILogger<MemoryGraphSink> logger)
    : IMemoryBatchSink
{
    /// <summary>Title-to-id map passed to the projector. Empty until the Vault index can resolve titles (see the remarks).</summary>
    private static readonly IReadOnlyDictionary<string, string> _noTitles = new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly IMemoryBatchSink _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly IMemoryGraphStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ILogger<MemoryGraphSink> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Wraps the real sink with the graph projection. The memory component wiring calls this helper when it lands;
    /// this slice does not register the decorator globally, because the real <see cref="IMemoryBatchSink"/> is wired elsewhere.
    /// </summary>
    public static IMemoryBatchSink Wrap(IMemoryBatchSink inner, IMemoryGraphStore store, ILogger<MemoryGraphSink> logger) =>
        new MemoryGraphSink(inner, store, logger);

    /// <inheritdoc />
    public async Task<MemoryCommitResult> CommitAsync(MemoryBatch batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var result = await _inner.CommitAsync(batch, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return result;
        }

        foreach (var item in batch.Items)
        {
            // The commit is already durable. Stop projecting when cancelled; the graph can be rebuilt from the Vault.
            if (ct.IsCancellationRequested)
            {
                LogStoppedByCancellation(_logger, result.CommitSha ?? string.Empty);
                break;
            }

            await ProjectItemAsync(batch, item, ct).ConfigureAwait(false);
        }

        return result;
    }

    private async Task ProjectItemAsync(MemoryBatch batch, MemoryWriteItem item, CancellationToken ct)
    {
        // Only notes in the writer's own private memory area are projected. Anything else is skipped silently.
        var scopePrefix = "agents/" + batch.AgentId + "/memory/";
        if (!item.Path.StartsWith(scopePrefix, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var parsed = NoteDocument.Parse(item.Content);
            if (!parsed.IsSuccess)
            {
                // The detail is structural and never quotes note content.
                LogSkippedUnparsable(_logger, item.WriteId, parsed.Error?.ToString() ?? "unknown");
                return;
            }

            var document = parsed.Document!;
            var revision = MemoryGraphProjector.Project(
                document.Note,
                document.Body,
                item.Path,
                batch.AgentId,
                _noTitles);

            await _store.StoreRevisionAsync(revision, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogStoreFailed(_logger, item.WriteId, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Memory graph projection stopped by cancellation after commit {CommitSha}.")]
    private static partial void LogStoppedByCancellation(ILogger logger, string commitSha);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Memory graph skipped write {WriteId}: note did not parse ({Error}).")]
    private static partial void LogSkippedUnparsable(ILogger logger, Guid writeId, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Memory graph failed to store write {WriteId}. The Vault commit stands.")]
    private static partial void LogStoreFailed(ILogger logger, Guid writeId, Exception ex);
}
