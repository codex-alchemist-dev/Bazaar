using Bazaar.Contracts.Gc;
using Bazaar.Server.Council.Service;
using Bazaar.Server.Gc.Write;

namespace Bazaar.Server.Host.CouncilAdapters;

/// <summary>
/// Production <see cref="ICouncilGcEvents"/> (doc 10, GC events): appends each <c>council.*</c> event to the vote's GC through
/// <see cref="IGcLogWriter"/>. The author is the Council's fixed system id and the idempotency key is the client message id, so
/// a retried service call appends nothing twice (the writer answers a repeat with the original event).
/// </summary>
public sealed class CouncilGcEventsAdapter(IGcLogWriter writer) : ICouncilGcEvents
{
    /// <summary>Author id of Council system events. Fixed so the per-author idempotency window is stable across restarts.</summary>
    public static readonly Guid SystemAuthorId = Guid.Parse("00000000-0000-7000-8000-0000000010c1");

    /// <inheritdoc />
    public async Task AppendAsync(Guid gcId, string kind, string payloadJson, string idempotencyKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (idempotencyKey.Length > AppendRequest.MaxClientMsgIdLength)
        {
            throw new ArgumentException(
                $"The idempotency key is longer than the {AppendRequest.MaxClientMsgIdLength} characters a GC client message id allows.",
                nameof(idempotencyKey));
        }

        var request = new AppendRequest(gcId, kind, AuthorKind.System, SystemAuthorId, SourceTrust.System, idempotencyKey)
        {
            PayloadJson = payloadJson,
        };
        await writer.AppendAsync(request, ct).ConfigureAwait(false);
    }
}
