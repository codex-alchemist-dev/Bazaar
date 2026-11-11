using Bazaar.Server.Agents.Turns.Start;

namespace Bazaar.Server.Kanban.Ryusei;

/// <summary>
/// The RyuseiFuda answer to <see cref="IWakeChainDepthSource"/> (doc 38, Chain depth). It answers only for <c>ryusei_*</c>
/// inbox items whose source ref is a fire id (the ping and wake writers set it to <c>fireId.ToString("D")</c>); everything else
/// returns null, so <see cref="TurnStarter"/> decides the depth for those.
/// </summary>
public sealed class RyuseiWakeChainDepthSource(IRyuseiWakeDepthSource depths) : IWakeChainDepthSource
{
    private const string _ryuseiPrefix = "ryusei_";

    public async Task<int?> ChainDepthAsync(string inboxKind, string? sourceRef, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inboxKind);
        if (!inboxKind.StartsWith(_ryuseiPrefix, StringComparison.Ordinal))
            return null;
        if (sourceRef is null || !Guid.TryParseExact(sourceRef, "D", out var fireId))
            return null;

        // Null here means no such fire row exists; the caller treats that as unreadable.
        return await depths.ChainDepthForFireAsync(fireId, ct).ConfigureAwait(false);
    }
}
